namespace InterCat.Cli;

/// <summary>
/// The files and folders a command writes where a person names them (R18): refused before anything is read or printed when
/// something stands in the way - a folder where a file goes, a file where a folder goes or above one - or when a file is
/// there and --overwrite was not given.
/// </summary>
internal static class OutputFiles
{
    /// <summary>
    /// Why <paramref name="path"/>, which <paramref name="option"/> named, may not be written; null when it may, or when no
    /// file was asked for. A file that stands there is <paramref name="replaced"/> only with --overwrite.
    /// </summary>
    public static string? Refusal(string? path, bool overwrite, string option = "--output", string replaced = "it") =>
        path is null ? null
        : Directory.Exists(path) ? $"{path} is a folder; {option} names the file to write, never a folder."
        : File.Exists(path) && !overwrite ? $"{path} exists. Pass --overwrite to replace {replaced}."
        : FileInTheWay(Path.GetDirectoryName(path)) is { } file ? $"{path} cannot be written: {file} is a file, not a folder."
        : null;

    /// <summary>
    /// Why the folder <paramref name="path"/> may not be made or written into, as <paramref name="purpose"/> says it is; null
    /// when nothing stands in the way. Whether a folder there may be used is the command's own to say.
    /// </summary>
    public static string? FolderRefusal(string path, string purpose) => FileInTheWay(path) is not { } file ? null
        : file == path ? $"{path} is a file, and {purpose}."
        : $"{path} cannot be made: {file} is a file, not a folder.";

    /// <summary>The file that stands at <paramref name="path"/> or where a folder above it would be; null when none does.</summary>
    private static string? FileInTheWay(string? path)
    {
        for (string? at = path; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (File.Exists(at))
            {
                return at;
            }

            if (Directory.Exists(at))
            {
                return null;
            }
        }

        return null;
    }
}
