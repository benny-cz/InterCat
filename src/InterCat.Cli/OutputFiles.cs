namespace InterCat.Cli;

/// <summary>
/// The file a command writes what it found to, as a person names it (R18): refused before anything is read or printed when
/// a folder stands there, which no file a command writes replaces, or when a file does and --overwrite was not given.
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
        : null;
}
