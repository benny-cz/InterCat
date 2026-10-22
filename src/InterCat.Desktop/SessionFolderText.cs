using System.Text;

namespace InterCat.Desktop;

/// <summary>
/// A file of the session as the capture card names it (§6.1): from the session's folder down, "…\journal\chunk-000017.icj",
/// since the card's path line names that folder whole beside Open folder. A reason that names a file of the session - one
/// another program held, one that could not be written - then reads in the detail's few lines, where the folder's whole
/// path would wrap through them and leave what the failure means below them.
/// </summary>
internal static class SessionFolderText
{
    /// <summary>
    /// The text with each path within <paramref name="sessionFolder"/> named from that folder down. A path is within it
    /// where the folder's own path begins one, followed by a separator; the folder alone, a folder whose name merely begins
    /// with the folder's, and a path that only ends in the folder's are named as they are.
    /// </summary>
    public static string Shortened(string text, string? sessionFolder)
    {
        ArgumentNullException.ThrowIfNull(text);
        string folder = sessionFolder?.TrimEnd('\\', '/') ?? string.Empty;
        if (folder.Length == 0) return text;

        var shortened = new StringBuilder(text.Length);
        int from = 0;
        for (int at = text.IndexOf(folder, StringComparison.OrdinalIgnoreCase); at >= 0;
            at = text.IndexOf(folder, at + folder.Length, StringComparison.OrdinalIgnoreCase))
        {
            int after = at + folder.Length;
            bool begins = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '\\' or '/');
            if (begins && after < text.Length && text[after] is '\\' or '/')
            {
                shortened.Append(text, from, at - from).Append('…');
                from = after;
            }
        }

        return from == 0 ? text : shortened.Append(text, from, text.Length - from).ToString();
    }
}
