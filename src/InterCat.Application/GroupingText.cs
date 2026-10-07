using System.Globalization;

namespace InterCat.Application;

/// <summary>
/// How a group of processes is named under a grouping, in one set of words for the window and the command line (R5).
/// </summary>
public static class GroupingText
{
    /// <summary>The processes whose lifecycle records named no terminal session, as one group names them.</summary>
    public const string SessionNotRecorded = "Terminal session not recorded";

    /// <summary>A terminal session's group: the processes whose lifecycle records name it.</summary>
    public static string Session(uint session) => string.Create(CultureInfo.InvariantCulture, $"Terminal session {session}");
}
