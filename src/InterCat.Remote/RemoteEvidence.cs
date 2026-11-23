namespace InterCat.Remote;

/// <summary>One file of a capture's evidence as its agent lists it: its name, its length and the SHA-256 of its bytes.</summary>
public sealed record RemoteEvidenceFile(string Name, long Length, byte[] Sha256)
{
    public const int Sha256Length = 32;
}

/// <summary>
/// A capture's evidence as its agent lists it (`remote-agent-v1` §4.5): the generation its pointer names, the pointer's own
/// bytes, and every file that generation needs - its manifest first, then each file the manifest names - with the length
/// and SHA-256 the evidence records for each.
/// </summary>
public sealed record RemoteEvidenceSnapshot(long Generation, byte[] PointerBytes, IReadOnlyList<RemoteEvidenceFile> Files)
{
    /// <summary>The most bytes a pointer is: a pointer names a generation, a manifest and its digest, in a few hundred.</summary>
    public const int MaximumPointerBytes = 1_024;

    public long TotalBytes => Files.Sum(file => file.Length);

    /// <summary>Why the listing is not one an agent serves or a controller takes, or null where it is.</summary>
    public string? Problem()
    {
        if (Generation < 1)
        {
            return "it names no published generation";
        }

        if (PointerBytes.Length is 0 or > MaximumPointerBytes)
        {
            return $"its pointer is not 1 to {MaximumPointerBytes:N0} bytes";
        }

        if (Files.Count == 0)
        {
            return "it names no file";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteEvidenceFile file in Files)
        {
            if (!RemoteEvidenceNames.IsFile(file.Name))
            {
                return $"'{RemoteEvidenceNames.Shown(file.Name)}' is not a name an evidence file has";
            }

            if (!names.Add(file.Name))
            {
                return $"it names '{file.Name}' twice";
            }

            if (file.Length < 0 || file.Sha256.Length != RemoteEvidenceFile.Sha256Length)
            {
                return $"'{file.Name}' has no length or no SHA-256";
            }
        }

        return null;
    }
}

/// <summary>
/// The evidence a capture left on its agent's host, read to serve it: the broker's evidence store in InterCat, plain files
/// in tests. A source only reads; the evidence is never written through it.
/// </summary>
public interface IRemoteEvidenceSource
{
    /// <summary>The evidence at <paramref name="directory"/>, as its current generation names it.</summary>
    /// <exception cref="InvalidDataException">The evidence published nothing, or does not verify.</exception>
    /// <exception cref="IOException">The evidence cannot be read.</exception>
    RemoteEvidenceSnapshot List(string directory);

    /// <summary>
    /// Reads into <paramref name="into"/> from <paramref name="offset"/> of a file its listing names, returning how many
    /// bytes it read: fewer than asked only at the file's end.
    /// </summary>
    int Read(string directory, string name, long offset, Span<byte> into);
}

/// <summary>
/// The names an evidence file has, on the wire and in a fetched folder: the session store's own rule for a file in a
/// session's folder - at most 64 ASCII letters, digits, '.', '-' and '_', beginning with neither '.' nor '-', ending
/// without '.', with no '..' and no reserved Windows device name - and neither the pointer's name, which a listing carries
/// apart, nor the fetch's own mark.
/// </summary>
public static class RemoteEvidenceNames
{
    /// <summary>The pointer's name in a session's folder, which a fetch writes last.</summary>
    public const string PointerFile = "current-generation.json";

    /// <summary>What a fetch keeps in its unfinished folder: whose capture it is fetching, and from which agent.</summary>
    public const string FetchMark = "remote-fetch-v1.json";

    public const int MaximumLength = 64;

    private static readonly string[] ReservedDeviceStems =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Whether <paramref name="name"/> is a name an evidence file may have.</summary>
    public static bool IsFile(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumLength
            || name.Equals(PointerFile, StringComparison.OrdinalIgnoreCase) || name.Equals(FetchMark, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        if (name[0] is '.' or '-' || name[^1] == '.' || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        int stemLength = name.IndexOf('.', StringComparison.Ordinal);
        return !ReservedDeviceStems.Contains(stemLength < 0 ? name : name[..stemLength], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A name as a message shows it: printable ASCII alone, at most 64 characters, whatever an agent sent.</summary>
    public static string Shown(string? name) =>
        new([.. (name ?? string.Empty).Take(MaximumLength).Select(character => character is >= ' ' and <= '~' ? character : '?')]);
}
