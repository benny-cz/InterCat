using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterCat.Storage;

/// <summary>What opening a session found, and what it had to do about it.</summary>
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

/// <summary>
/// One file staged for a generation. It is written under a unique staging name, flushed to the device
/// and measured; publication renames it. Until then it is not part of any generation, and a crash
/// leaves it as an unreferenced staging file that the next open removes.
/// </summary>
public sealed class StoreStagingFile : IDisposable
{
    private readonly FileStream stream;
    private bool completed;
    private bool disposed;

    internal StoreStagingFile(
        string stagingName,
        string publishedName,
        StoreDependencyKind kind,
        FileStream stream)
    {
        StagingName = stagingName;
        PublishedName = publishedName;
        Kind = kind;
        this.stream = stream;
    }

    public string StagingName { get; }

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
        if (!completed)
        {
            stream.Dispose();
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
    public const string StagingPrefix = "stg-";

    public const string StagingSuffix = ".tmp";

    /// <summary>How many files one generation may publish at once.</summary>
    public const int MaximumStagedFiles = 4_096;

    private readonly IOwnedDirectory directory;
    private readonly Lock gate = new();
    private SessionManifestV1? current;

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
                return (current?.Generation ?? 0) + 1;
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

        string stagingName = string.Create(
            CultureInfo.InvariantCulture,
            $"{StagingPrefix}{Guid.NewGuid():N}{StagingSuffix}");
        FileStream stream = directory.OpenOwnedFile(
            stagingName,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.WriteThrough);
        return new(stagingName, publishedName, kind, stream);
    }

    /// <summary>
    /// Publishes one generation. Every staged file must be complete; a name an earlier generation
    /// already published is refused, because a published file is immutable.
    /// </summary>
    public StoreCommitResult Commit(
        IReadOnlyList<StoreStagingFile> staged,
        CommittedBoundary boundary,
        DateTimeOffset committedUtc,
        CancellationToken cancellationToken = default)
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
            long previousGeneration = current?.Generation ?? 0;
            List<StoreDependency> carried = [.. current?.Dependencies ?? []];
            var names = new HashSet<string>(
                carried.Select(dependency => dependency.Name),
                StringComparer.OrdinalIgnoreCase);
            var published = new List<StoreDependency>(staged.Count);
            foreach (StoreStagingFile file in staged)
            {
                StoreDependency dependency = file.Dependency
                    ?? throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' was not completed, so its contents are not "
                        + "durable and it cannot be published.");
                if (!names.Add(dependency.Name))
                {
                    throw new InvalidOperationException(
                        $"Generation {previousGeneration + 1} would republish '{dependency.Name}', which "
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
                previousGeneration + 1,
                SessionId,
                committedUtc,
                SourceIdentity,
                previousGeneration == 0 ? null : previousGeneration,
                boundary,
                [.. carried, .. published]);

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
            return new(manifest, [.. published.Select(dependency => dependency.Name)], previousGeneration);
        }
    }

    /// <summary>
    /// Removes files no generation references. It is separate from opening on purpose: a file that is
    /// unreferenced now may be a dependency of a generation whose publication was interrupted, and
    /// deleting it would turn a recoverable interruption into lost evidence. Staging files are the one
    /// exception, and opening removes those by itself.
    /// </summary>
    public IReadOnlyList<string> RemoveOrphans()
    {
        lock (gate)
        {
            var removed = new List<string>();
            foreach (string name in Orphans(directory, current))
            {
                if (directory.RemoveOwnedFile(name))
                {
                    removed.Add(name);
                }
            }

            return removed;
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
        foreach (string name in List(directory))
        {
            if (IsStaging(name))
            {
                if (directory.RemoveOwnedFile(name))
                {
                    removed.Add(name);
                }

                continue;
            }

            if (IsReferenced(manifest, name))
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

    private static IEnumerable<string> Orphans(IOwnedDirectory directory, SessionManifestV1? manifest) =>
        List(directory).Where(name => !IsStaging(name) && !IsReferenced(manifest, name));

    private static bool IsReferenced(SessionManifestV1? manifest, string name) =>
        name.Equals(SessionPointerV1.FileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(SessionPointerV1.PreviousFileName, StringComparison.OrdinalIgnoreCase)
        || (manifest is not null
            && (name.Equals(SessionManifestV1.FileNameFor(manifest.Generation), StringComparison.OrdinalIgnoreCase)
                || manifest.Dependencies.Any(dependency =>
                    dependency.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));

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
