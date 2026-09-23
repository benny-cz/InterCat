using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterCat.Storage;

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
public sealed class SessionStore
{
    public const string PublicationLockFileName = "session-publication.lock";

    public const string EvidenceLeaseLockFileName = "session-evidence-lease.lock";

    public const string StagingPrefix = "stg-";

    public const string StagingSuffix = ".tmp";

    public const string StagingOwnershipSuffix = ".lease";

    /// <summary>How many files one generation may publish at once.</summary>
    public const int MaximumStagedFiles = 4_096;

    private readonly IOwnedDirectory directory;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, EvidenceLease> leases = [];
    private SessionManifestV1? current;
    private SessionManifestV1? publicationBaseline;

    private SessionStore(
        IOwnedDirectory directory,
        Guid sessionId,
        string sourceIdentity,
        SessionManifestV1? current,
        StoreRecoveryReport recovery)
    {
        this.directory = directory;
        SessionId = sessionId;
        SourceIdentity = sourceIdentity;
        this.current = current;
        publicationBaseline = current;
        Recovery = recovery;
    }

    public Guid SessionId { get; }

    public string SourceIdentity { get; }

    public StoreRecoveryReport Recovery { get; }

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

        (SessionManifestV1? manifest, bool rolledBack, string? reason) = Acquire(directory);
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
            new(manifest?.Generation ?? 0, rolledBack, reason, removed, orphans, orphanBytes));
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
    /// Opens a session for reading without being told which session it is. The identity comes from the
    /// generation the pointer names, so a viewer can open a directory it was handed. A store opened this way
    /// publishes nothing: a generation names the session it belongs to, and this one was not told which.
    /// </summary>
    public static SessionStore OpenExisting(IOwnedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        (SessionManifestV1? manifest, bool rolledBack, string? reason) = Acquire(directory);
        (IReadOnlyList<string> removed, IReadOnlyList<string> orphans, long orphanBytes) =
            Sweep(directory, manifest);
        return new(
            directory,
            manifest?.SessionId ?? Guid.Empty,
            manifest?.SourceIdentity ?? string.Empty,
            manifest,
            new(manifest?.Generation ?? 0, rolledBack, reason, removed, orphans, orphanBytes));
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
            || publishedName.StartsWith("manifest-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A dependency cannot use the session's pointer, manifest or publication-lock namespace.",
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
        CommitCore(staged, boundary, committedUtc, replaceDerived: false, cancellationToken,
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
        CommitCore(staged, boundary, committedUtc, replaceDerived: true, cancellationToken, sourceGeneration,
            expectedGeneration);

    private StoreCommitResult CommitCore(
        IReadOnlyList<StoreStagingFile> staged,
        CommittedBoundary boundary,
        DateTimeOffset committedUtc,
        bool replaceDerived,
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
            if (replaceDerived)
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

                // The admitted evidence - every journal chunk - and the plan and coverage ledger that describe the
                // capture are not derivations of it, so a replacement derivation carries them all unchanged. Carrying
                // every journal means no chunk's evidence is ever dropped by replacing the rows derived from it.
                carried =
                [
                    .. previous.Dependencies.Where(dependency =>
                        dependency.Kind is StoreDependencyKind.DerivationPlan
                            or StoreDependencyKind.CoverageLedger
                            or StoreDependencyKind.Journal),
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
                carried = [.. current?.Dependencies ?? []];
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
                [.. carried, .. published]);
            RequireUnpublishedTarget(SessionManifestV1.FileNameFor(manifest.Generation));

            // The manifest may reference only durable dependencies, so every one of them is read back
            // and measured before it is named. A rename is not a power-failure guarantee (§20.1).
            string? unverifiable = VerifyDependencies(directory, manifest);
            if (unverifiable is not null)
            {
                throw new IOException(
                    $"Generation {manifest.Generation} was not published: {unverifiable}");
            }

            Write(directory, SessionManifestV1.FileNameFor(manifest.Generation), manifest, SessionManifestV1.Json);
            RetainPreviousPointer(directory);
            Write(directory, SessionPointerV1.FileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
            current = manifest;
            publicationBaseline = manifest;
            foreach (StoreStagingFile file in staged)
            {
                file.MarkPublished();
            }

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
        lock (gate)
        {
            // Hold the shared marker before reading the pointer. Retention may publish concurrently,
            // but it cannot remove any old dependency while this handle is open. Readers need only
            // read access to the broker-owned root; they never take the writer's publication lock.
            FileStream hold = AcquireEvidenceReadHold();
            try
            {
                (SessionManifestV1? disk, _, _) = Acquire(directory);
                SessionManifestV1 manifest = disk
                    ?? throw new InvalidOperationException(
                        "This session has published no generation, so there is nothing to acquire. An empty "
                        + "session is not a generation with no data.");
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

                current = manifest;

                // A pin cannot promise less disk allowance than the evidence it already holds.
                long held = manifest.Dependencies.Sum(dependency => dependency.LengthBytes);
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

                released.Add(dependency);
            }

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

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 manifest = current
                ?? throw new InvalidOperationException("This session has published no generation to retain from.");
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
            (SessionManifestV1? manifest, bool rolledBack, string? reason) = Acquire(directory);
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
            string? verificationProblem = TryAcquire(directory, SessionPointerV1.FileName, out SessionManifestV1? repaired);
            if (verificationProblem is not null || repaired?.Digest != manifest.Digest)
            {
                throw new IOException(
                    $"Pointer repair was written but did not verify ({verificationProblem ?? "generation mismatch"}). "
                    + "Do not publish until the session is inspected again.");
            }

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
            record);
        string? unverifiable = VerifyDependencies(directory, manifest);
        if (unverifiable is not null)
        {
            throw new IOException($"Generation {manifest.Generation} was not published: {unverifiable}");
        }

        Write(directory, SessionManifestV1.FileNameFor(manifest.Generation), manifest, SessionManifestV1.Json);
        RetainPreviousPointer(directory);
        Write(directory, SessionPointerV1.FileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
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

        return new(manifest, [.. released.Select(dependency => dependency.Name)], removed, heldByLease, reclaimed);
    }

    private bool IsLeased(string name, DateTimeOffset now) =>
        leases.Values.Any(lease => lease.Holds(name, now));

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
    }

    private static (SessionManifestV1? Manifest, bool RolledBack, string? Reason) Acquire(IOwnedDirectory directory)
    {
        string? currentProblem = TryAcquire(directory, SessionPointerV1.FileName, out SessionManifestV1? manifest);
        if (currentProblem is null)
        {
            return (manifest, false, null);
        }

        string? previousProblem = TryAcquire(directory, SessionPointerV1.PreviousFileName, out manifest);
        if (previousProblem is null && manifest is not null)
        {
            return (manifest, true, currentProblem);
        }

        // A session whose pointer names nothing is empty; one whose pointer names something that does
        // not verify, with no usable last-known-good, is refused rather than silently reset.
        return Exists(directory, SessionPointerV1.FileName) || Exists(directory, SessionPointerV1.PreviousFileName)
            ? throw new InvalidDataException(
                $"This session has no complete generation. Its pointer is unusable ({currentProblem}) and "
                + $"its last-known-good is too ({previousProblem ?? "it names no generation"}).")
            : (null, false, null);
    }

    private static string? TryAcquire(
        IOwnedDirectory directory,
        string pointerName,
        out SessionManifestV1? manifest)
    {
        manifest = null;
        SessionPointerV1? pointer = Read<SessionPointerV1>(directory, pointerName);
        if (pointer is null)
        {
            return $"'{pointerName}' is missing or unreadable";
        }

        string? problem = pointer.Validate();
        if (problem is not null)
        {
            return problem;
        }

        SessionManifestV1? candidate = Read<SessionManifestV1>(directory, pointer.ManifestName);
        if (candidate is null)
        {
            return $"generation {pointer.Generation} names manifest '{pointer.ManifestName}', which is "
                + "missing or unreadable";
        }

        problem = candidate.Validate() ?? candidate.VerifyDigest();
        if (problem is not null)
        {
            return problem;
        }

        if (!string.Equals(candidate.Digest, pointer.ManifestDigest, StringComparison.Ordinal))
        {
            return $"the pointer expects manifest digest {pointer.ManifestDigest} but '{pointer.ManifestName}' "
                + $"carries {candidate.Digest}";
        }

        if (candidate.Generation != pointer.Generation)
        {
            return $"the pointer names generation {pointer.Generation} but its manifest is "
                + $"generation {candidate.Generation}";
        }

        problem = VerifyDependencies(directory, candidate);
        if (problem is not null)
        {
            return problem;
        }

        manifest = candidate;
        return null;
    }

    private static string? VerifyDependencies(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
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
                    return $"dependency '{dependency.Name}' is {stream.Length} bytes where generation "
                        + $"{manifest.Generation} recorded {dependency.LengthBytes}";
                }

                string digest = string.Concat("sha256:", Convert.ToHexStringLower(SHA256.HashData(stream)));
                if (!string.Equals(digest, dependency.Digest, StringComparison.Ordinal))
                {
                    return $"dependency '{dependency.Name}' computes {digest} where generation "
                        + $"{manifest.Generation} recorded {dependency.Digest}";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"dependency '{dependency.Name}' could not be read: {exception.Message}";
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
    /// Every file the session still needs: both pointers, and the manifest and dependencies of each
    /// generation a pointer names.
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
        catch (IOException exception)
        {
            throw new IOException(
                "Could not acquire the session's shared evidence lease guard. Retention may be removing "
                + "released files; retry once it finishes.", exception);
        }
    }

    private void RequireFreshCurrent()
    {
        (SessionManifestV1? disk, bool rolledBack, _) = Acquire(directory);
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
    /// </summary>
    private static void Write<T>(IOwnedDirectory directory, string name, T document, JsonSerializerOptions options)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, options));
        string stagingName = string.Create(
            CultureInfo.InvariantCulture,
            $"{StagingPrefix}{Guid.NewGuid():N}{StagingSuffix}");
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
