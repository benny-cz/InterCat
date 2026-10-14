namespace InterCat.Desktop.Presentation;

/// <summary>
/// Why an investigation could not be read or changed, in its windows' words: a file no longer where the window has it is
/// said to be gone, never with the command line's advice to make one, and anything else as the investigation says it.
/// </summary>
internal static class InvestigationTrouble
{
    public static string Why(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return File.Exists(path)
            ? exception.Message
            : $"No file is at {path} any more; it was moved, renamed or deleted.";
    }
}
