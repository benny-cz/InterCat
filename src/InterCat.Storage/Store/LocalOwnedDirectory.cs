namespace InterCat.Storage;

/// <summary>
/// An ordinary directory used as an owned root. It is the unprivileged half of the same boundary the
/// broker's validated root provides: the name rules and the reparse refusal are identical, and what it
/// does not have is the broker root's protected security and held handle. Anything a viewer or an
/// importer writes goes here; anything a privileged capture writes goes through the broker's root.
/// </summary>
public sealed class LocalOwnedDirectory : IOwnedDirectory
{
    private LocalOwnedDirectory(string path) => Path = path;

    public string Path { get; }

    /// <summary>Opens an existing directory as an owned root, refusing a redirected one.</summary>
    public static LocalOwnedDirectory Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The session directory '{full}' does not exist.");
        }

        return (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0
            ? throw new IOException(
                $"The session directory '{full}' is a reparse point; a redirected root is refused rather "
                + "than followed.")
            : new(full);
    }

    /// <summary>Creates the directory if it is absent and opens it.</summary>
    public static LocalOwnedDirectory OpenOrCreate(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(System.IO.Path.GetFullPath(directory));
        return Open(directory);
    }

    /// <inheritdoc />
    public FileStream OpenOwnedFile(
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        string path = Resolve(name, nameof(name));
        RefuseReparsePoint(path);
        return new(path, mode, access, share, 4096, options);
    }

    /// <inheritdoc />
    public void ReplaceOwnedFile(string sourceName, string destinationName)
    {
        string source = Resolve(sourceName, nameof(sourceName));
        string destination = Resolve(destinationName, nameof(destinationName));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An owned file is never replaced by itself.",
                nameof(destinationName));
        }

        RefuseReparsePoint(source);
        RefuseReparsePoint(destination);
        File.Move(source, destination, overwrite: true);
    }

    /// <inheritdoc />
    public bool RemoveOwnedFile(string name)
    {
        string path = Resolve(name, nameof(name));
        if (Directory.Exists(path))
        {
            throw new IOException(
                $"'{path}' is a directory beneath the session root and is refused rather than deleted.");
        }

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>One enumeration: the entries it returns carry their length, times and attributes.</remarks>
    public IReadOnlyDictionary<string, OwnedFileFacts> DescribeOwnedFiles()
    {
        var facts = new Dictionary<string, OwnedFileFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in new DirectoryInfo(Path).EnumerateFiles())
        {
            if (OwnedFileName.Validate(file.Name) is null)
            {
                facts[file.Name] = new(
                    file.Length,
                    file.LastWriteTimeUtc.Ticks,
                    (file.Attributes & FileAttributes.ReparsePoint) != 0);
            }
        }

        return facts;
    }

    /// <summary>The owned file names directly beneath this root, in ordinal order.</summary>
    public IReadOnlyList<string> ListOwnedFiles() =>
    [
        .. Directory.EnumerateFiles(Path)
            .Select(System.IO.Path.GetFileName)
            .Where(name => name is not null && OwnedFileName.Validate(name) is null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal),
    ];

    private string Resolve(string name, string parameterName)
    {
        OwnedFileName.Require(name, parameterName);
        return System.IO.Path.Combine(Path, name);
    }

    private static void RefuseReparsePoint(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"'{path}' is a reparse point and is never written through.");
        }
    }
}
