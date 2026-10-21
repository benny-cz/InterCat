namespace InterCat.Storage;

/// <summary>What the folder an import was told to publish into holds (§18.4, contracts/import-v1.md §8).</summary>
public enum ImportDestinationKind
{
    /// <summary>
    /// No folder yet, an empty one, or one holding nothing but a session's lock files: nothing was published or staged
    /// there, so the import publishes its session into it.
    /// </summary>
    New = 1,

    /// <summary>
    /// A completed import of this evidence: the session whose identity the import derives from the evidence's bytes, its
    /// retained-evidence policy and its contract versions, with a generation that verifies. The import reuses it rather
    /// than reading the evidence again.
    /// </summary>
    ThisImport = 2,

    /// <summary>A session of another identity: an import of other bytes or under another contract, a recording, a package.</summary>
    AnotherSession = 3,

    /// <summary>A session no generation of which verifies.</summary>
    Unreadable = 4,

    /// <summary>
    /// No generation, and files a publication staged that no writer holds any more: what an import or a recording that
    /// stopped before it published left.
    /// </summary>
    Unfinished = 5,

    /// <summary>No generation yet, and files a writer is staging there now.</summary>
    Publishing = 6,

    /// <summary>Files or folders that are not a session's, or a file where the folder would be.</summary>
    Occupied = 7,
}

/// <summary>
/// What the folder an import was told to publish into holds, read before the evidence is. An import publishes only into a
/// new folder, so it never counts one capture twice or mixes its evidence with anything else; a folder that already holds
/// this import is reused rather than imported into again (§18.4); and anything else is refused with what was found there.
/// Looking writes nothing.
/// </summary>
public sealed record ImportDestination
{
    /// <summary>The lock files a session's folder holds before anything is published in it: a cleanup of what an
    /// unfinished publication staged takes the publication lock and leaves its file behind.</summary>
    private static readonly HashSet<string> LockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SessionStore.PublicationLockFileName,
        SessionStore.EvidenceLeaseLockFileName,
        RetentionPinsV1.LockFileName,
    };

    public required string Path { get; init; }

    public required ImportDestinationKind Kind { get; init; }

    /// <summary>The generation the folder's session reads: this import's, or another session's.</summary>
    public SessionManifestV1? Manifest { get; init; }

    /// <summary>
    /// Why this import's session reads its last-known-good generation rather than the newest its pointer names, or why no
    /// generation of an unreadable session verifies; otherwise null.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// By name, the entries that are not a session's (<see cref="ImportDestinationKind.Occupied"/>) or the files staged
    /// there (<see cref="ImportDestinationKind.Unfinished"/>, <see cref="ImportDestinationKind.Publishing"/>).
    /// </summary>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>What the staged files hold, in bytes (<see cref="ImportDestinationKind.Unfinished"/>,
    /// <see cref="ImportDestinationKind.Publishing"/>).</summary>
    public long StagedBytes { get; init; }

    /// <summary>
    /// Reads what <paramref name="path"/> holds. <paramref name="importSession"/> derives the session identity of the
    /// import, which hashes its evidence, so it is called only when the folder holds a session to compare it with. This
    /// import's session is verified - every file its generation names is hashed - before it is said to be reusable.
    /// </summary>
    public static ImportDestination Inspect(string path, Func<Guid> importSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(importSession);
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (File.Exists(full))
        {
            return new() { Path = full, Kind = ImportDestinationKind.Occupied, Entries = [System.IO.Path.GetFileName(full)] };
        }

        if (!Directory.Exists(full))
        {
            return new() { Path = full, Kind = ImportDestinationKind.New };
        }

        string[] entries =
        [
            .. Directory.EnumerateFileSystemEntries(full)
                .Select(entry => System.IO.Path.GetFileName(entry))
                .Order(StringComparer.Ordinal),
        ];
        if (entries.Contains(SessionPointerV1.FileName, StringComparer.OrdinalIgnoreCase)
            || entries.Contains(SessionPointerV1.PreviousFileName, StringComparer.OrdinalIgnoreCase))
        {
            return Session(full, importSession);
        }

        // A folder inside is never a session's, whatever it is named.
        string[] others =
        [
            .. entries.Where(name => !(LockNames.Contains(name) || SessionStore.IsStagedName(name))
                || Directory.Exists(System.IO.Path.Combine(full, name))),
        ];
        if (others.Length > 0)
        {
            return new() { Path = full, Kind = ImportDestinationKind.Occupied, Entries = others };
        }

        string[] staged = [.. entries.Where(SessionStore.IsStagedName)];
        if (staged.Length == 0)
        {
            return new() { Path = full, Kind = ImportDestinationKind.New };
        }

        // A folder no generation was published in opens as a store of no session, which can still say which staged files
        // a writer holds; it never removes one.
        StagingCleanupReport staging = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full)).PreviewStagingCleanup();
        return new()
        {
            Path = full,
            Kind = staging.ActiveFiles.Count > 0 ? ImportDestinationKind.Publishing : ImportDestinationKind.Unfinished,
            Entries = staged,
            StagedBytes = staged.Sum(name => LengthOf(System.IO.Path.Combine(full, name))),
        };
    }

    private static ImportDestination Session(string full, Func<Guid> importSession)
    {
        SessionManifestV1? viewed;
        try
        {
            viewed = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(full)).Current;
        }
        catch (InvalidDataException exception)
        {
            return new() { Path = full, Kind = ImportDestinationKind.Unreadable, Reason = exception.Message };
        }

        if (viewed is null)
        {
            // Its pointers were removed between the listing and the read.
            return new() { Path = full, Kind = ImportDestinationKind.Unreadable, Reason = SessionStore.SessionGone };
        }

        if (viewed.SessionId != importSession())
        {
            return new() { Path = full, Kind = ImportDestinationKind.AnotherSession, Manifest = viewed };
        }

        SessionStore verified;
        try
        {
            verified = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        }
        catch (InvalidDataException exception)
        {
            return new() { Path = full, Kind = ImportDestinationKind.Unreadable, Manifest = viewed, Reason = exception.Message };
        }

        // Another writer can have published into the folder since it was viewed; what verifies is what is reused.
        return verified.Current switch
        {
            null => new() { Path = full, Kind = ImportDestinationKind.Unreadable, Reason = SessionStore.SessionGone },
            { } manifest when manifest.SessionId == viewed.SessionId => new()
            {
                Path = full, Kind = ImportDestinationKind.ThisImport, Manifest = manifest, Reason = verified.RollbackReason,
            },
            { } manifest => new() { Path = full, Kind = ImportDestinationKind.AnotherSession, Manifest = manifest },
        };
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // A staged file its writer removed since the folder was listed holds nothing any more.
            return 0;
        }
    }
}
