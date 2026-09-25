namespace InterCat.Storage;

/// <summary>
/// A directory whose boundary was validated and is still held. Files are opened through this interface
/// rather than from a composed path string, so a destination can never leave the validated root and a
/// validated root can never be renamed out from under it. The privileged broker root and an ordinary
/// unprivileged session directory are both this, which is why the store's commit protocol does not need
/// to know which one it is writing into.
/// </summary>
public interface IOwnedDirectory
{
    /// <summary>The validated final path of the root, as Windows resolved it from the open handle.</summary>
    string Path { get; }

    /// <summary>
    /// Opens one file directly beneath the validated root. The name must be a single ordinary
    /// component; a reparse point, a directory, another volume or a different final path is refused
    /// rather than written to.
    /// </summary>
    FileStream OpenOwnedFile(
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options);

    /// <summary>
    /// Replaces one owned file with another in a single directory operation, so a reader either sees
    /// the previous complete file or the new complete one and never an absent or half-written name.
    /// </summary>
    void ReplaceOwnedFile(string sourceName, string destinationName);

    /// <summary>
    /// Removes one owned file if it is there. Returns whether a file was removed; a directory beneath
    /// the root is refused rather than deleted, because only the broker creates entries there.
    /// </summary>
    bool RemoveOwnedFile(string name);

    /// <summary>
    /// What one listing of the root says about each owned file beneath it, without opening any, or null when this
    /// directory cannot list them. A listing can trail a write still in progress, so it may only confirm what an
    /// earlier measurement of the file found, never stand in for one (ADR-025).
    /// </summary>
    IReadOnlyDictionary<string, OwnedFileFacts>? DescribeOwnedFiles() => null;
}

/// <summary>A file's length, last-write time and reparse state as a directory listing states them.</summary>
public readonly record struct OwnedFileFacts(long LengthBytes, long LastWriteUtcTicks, bool IsReparsePoint);

/// <summary>
/// The only names that are opened beneath an owned root. The rule is an allow list of ordinary
/// characters rather than a search for traversal sequences, because a deny list has to anticipate every
/// encoding of a separator and an allow list does not.
/// </summary>
public static class OwnedFileName
{
    public const int MaximumLength = 64;

    private static readonly string[] ReservedDeviceStems =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Returns the reason the name is not an owned file name, or null when it is one.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "An owned file name is required.";
        }

        if (name.Length > MaximumLength)
        {
            return $"An owned file name is at most {MaximumLength} characters; '{name.Length}' were supplied.";
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return "An owned file name allows only ASCII letters, digits, '.', '-' and '_'.";
            }
        }

        if (name[0] is '.' or '-' || name[^1] is '.' or ' ')
        {
            return "An owned file name cannot begin with '.' or '-', or end with '.'.";
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "An owned file name cannot contain '..'.";
        }

        int stemLength = name.IndexOf('.');
        string stem = stemLength < 0 ? name : name[..stemLength];
        return ReservedDeviceStems.Contains(stem, StringComparer.OrdinalIgnoreCase)
            ? $"'{stem}' is a reserved Windows device name and is never opened as an owned file."
            : null;
    }

    public static void Require(string? name, string parameterName)
    {
        string? problem = Validate(name);
        if (problem is not null)
        {
            throw new ArgumentException(problem, parameterName);
        }
    }
}
