using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>One completed import the catalogue lists (contracts/import-v1.md §8.1).</summary>
public sealed record ImportCatalogueEntry
{
    /// <summary>The import's identity (§2): the file's bytes, the evidence kept and the contract versions.</summary>
    public required string ImportDigest { get; init; }

    /// <summary>The identity of the session the import publishes, which is derived from <see cref="ImportDigest"/>.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The folder holding the session, as a full path.</summary>
    public required string Path { get; init; }

    /// <summary>Where the file was read from when it was imported.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The file's length, which finds the entries an import of the same bytes is among without hashing.</summary>
    public required long SourceLength { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

/// <summary>
/// The user's catalogue of completed imports (contracts/import-v1.md §8.1): where each import's session is, so an import
/// of the same evidence into a new folder finds the folder that already holds it rather than reading the file again
/// (§18.4). It is a hint, never evidence: nothing it lists is reused before its folder verifies as holding the import, and
/// a file that cannot be read as a catalogue of this contract lists nothing and is never replaced. It keeps the newest
/// <see cref="MaximumEntries"/> entries.
/// </summary>
public sealed class ImportCatalogue
{
    public const string FileName = "import-catalogue-v1.json";

    public const string Contract = "import-catalogue-v1";

    /// <summary>How many imports the catalogue keeps, the most recently recorded; an older one is imported again.</summary>
    public const int MaximumEntries = 1_000;

    /// <summary>The longest catalogue read: far beyond what its entries take.</summary>
    public const long MaximumBytes = 16L * 1024 * 1024;

    /// <summary>How long a writer waits for another to finish writing the catalogue.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private ImportCatalogue(string path, IReadOnlyList<ImportCatalogueEntry> entries, string? problem)
    {
        Path = path;
        Entries = entries;
        Problem = problem;
    }

    /// <summary>The catalogue's file.</summary>
    public string Path { get; }

    /// <summary>What it lists, the most recently recorded first.</summary>
    public IReadOnlyList<ImportCatalogueEntry> Entries { get; }

    /// <summary>
    /// Why the file is not read as a catalogue - it cannot be read, is not a catalogue of imports, or is one of another
    /// contract - so it lists nothing and is left as it is; null when it was read or is not there yet.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// The catalogue in the user's own InterCat folder, beside the window's sessions; null where the system provides no
    /// local user-data folder.
    /// </summary>
    public static string? DefaultPath()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(userData) ? null : System.IO.Path.Combine(userData, "InterCat", FileName);
    }

    /// <summary>A folder's path as the catalogue keeps and compares it: full, without a trailing separator.</summary>
    public static string FolderOf(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    /// <summary>How the system compares folder paths: whatever their case on Windows, exactly elsewhere.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reads the catalogue at <paramref name="path"/>; one not there yet lists nothing.</summary>
    public static ImportCatalogue Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        try
        {
            if (!File.Exists(full))
            {
                return new(full, [], null);
            }

            // Shared with a writer moving a new catalogue over it, which then never waits for this read.
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumBytes)
            {
                return new(full, [], $"{full} is larger than a catalogue of imports grows, so it is not one");
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Parse(full, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(full, [], $"{full} could not be read: {exception.Message}");
        }
    }

    /// <summary>The entries of a source <paramref name="length"/> bytes long, the most recently recorded first.</summary>
    public IReadOnlyList<ImportCatalogueEntry> OfLength(long length) => [.. Entries.Where(entry => entry.SourceLength == length)];

    /// <summary>
    /// Records <paramref name="entry"/>, in place of any entry of the same folder, and forgets the entries of
    /// <paramref name="forget"/>'s folders, keeping the newest <see cref="MaximumEntries"/>. The catalogue is read again
    /// and replaced whole while no other writer holds it, so two imports recording at once both stay listed, and a reader
    /// meets the old catalogue or the new one, never half of either. A file that is not read as a catalogue is left as it
    /// is, and the returned catalogue says why. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> where the file cannot be written.
    /// </summary>
    public static ImportCatalogue Update(string path, ImportCatalogueEntry? entry, IReadOnlyCollection<string> forget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(forget);
        string full = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        using FileStream held = Hold(full + ".lock");
        ImportCatalogue current = Read(full);
        if (current.Problem is not null)
        {
            return current;
        }

        StringComparer paths = PathComparer;
        var forgotten = new HashSet<string>(forget.Select(FolderOf), paths);
        var kept = new List<ImportCatalogueEntry>(current.Entries.Count + 1);
        if (entry is not null)
        {
            kept.Add(entry with { Path = FolderOf(entry.Path) });
            forgotten.Add(kept[0].Path);
        }

        kept.AddRange(current.Entries.Where(listed => !forgotten.Contains(listed.Path)));
        ImportCatalogueEntry[] written = [.. kept.Take(MaximumEntries)];
        var file = new CatalogueFile
        {
            Contract = Contract,
            Imports = [.. written.Select(listed => new CatalogueFileEntry
            {
                ImportDigest = listed.ImportDigest,
                SessionId = listed.SessionId.ToString("N"),
                Path = listed.Path,
                SourcePath = listed.SourcePath,
                SourceLength = listed.SourceLength,
                RecordedUtc = listed.RecordedUtc,
            })],
        };

        // Written beside it and moved over it, so a reader meets the old catalogue or the new one.
        string staged = System.IO.Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, Json);
                stream.Flush(flushToDisk: true);
            }

            Replace(staged, full);
        }
        finally
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }

        return new(full, written, null);
    }

    private static ImportCatalogue Parse(string full, byte[] bytes)
    {
        CatalogueFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogueFile>(bytes, Json);
        }
        catch (JsonException exception)
        {
            return new(full, [], $"{full} is not a catalogue of imports: {exception.Message}");
        }

        if (file is null || string.IsNullOrEmpty(file.Contract))
        {
            return new(full, [], $"{full} is not a catalogue of imports: it names no contract");
        }

        if (file.Contract != Contract)
        {
            return new(full, [], $"{full} is a catalogue of {file.Contract}, which this InterCat does not read");
        }

        // An entry that does not say what it must is passed over; the others are listed, newest first.
        var entries = new List<ImportCatalogueEntry>();
        foreach (CatalogueFileEntry? listed in file.Imports ?? [])
        {
            if (listed is { ImportDigest: { Length: > 0 } digest, Path: { Length: > 0 } folder, SourcePath: { Length: > 0 } source }
                && Guid.TryParseExact(listed.SessionId, "N", out Guid session) && listed.SourceLength >= 0
                && System.IO.Path.IsPathFullyQualified(folder))
            {
                entries.Add(new()
                {
                    ImportDigest = digest,
                    SessionId = session,
                    Path = FolderOf(folder),
                    SourcePath = source,
                    SourceLength = listed.SourceLength,
                    RecordedUtc = listed.RecordedUtc,
                });
            }
        }

        return new(full, [.. entries.OrderByDescending(entry => entry.RecordedUtc)], null);
    }

    /// <summary>
    /// The catalogue's lock, held while it is read and replaced; another writer waits for it a while. The lock file stays,
    /// so every writer locks the same file.
    /// </summary>
    private static FileStream Hold(string path)
    {
        DateTime until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Moves <paramref name="staged"/> over <paramref name="full"/>, waiting a while for a reader that holds it.</summary>
    private static void Replace(string staged, string full)
    {
        DateTime until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                File.Move(staged, full, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(25);
            }
        }
    }

    private sealed record CatalogueFile
    {
        public string? Contract { get; init; }

        public IReadOnlyList<CatalogueFileEntry?>? Imports { get; init; }
    }

    private sealed record CatalogueFileEntry
    {
        public string? ImportDigest { get; init; }

        public string? SessionId { get; init; }

        public string? Path { get; init; }

        public string? SourcePath { get; init; }

        public long SourceLength { get; init; }

        public DateTimeOffset RecordedUtc { get; init; }
    }
}
