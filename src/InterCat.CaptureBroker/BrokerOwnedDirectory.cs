namespace InterCat.CaptureBroker;

/// <summary>
/// A directory whose security boundary the broker validated and still holds open. Privileged broker
/// files are opened through this interface rather than from a composed path string, so a destination
/// can never leave the validated root and a validated root can never be renamed out from under it.
/// </summary>
public interface IBrokerOwnedDirectory
{
    /// <summary>The validated final path of the root, as Windows resolved it from the open handle.</summary>
    string Path { get; }

    /// <summary>The volume the validated root lives on. Every owned file must report the same volume.</summary>
    uint VolumeSerialNumber { get; }

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
}

/// <summary>
/// The only names the broker will open beneath its root. The rule is an allow list of ordinary
/// characters rather than a search for traversal sequences, because a deny list has to anticipate
/// every encoding of a separator and an allow list does not.
/// </summary>
public static class BrokerOwnedFileName
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
            ? $"'{stem}' is a reserved Windows device name and is never opened as a broker file."
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
