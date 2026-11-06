using System.Globalization;

namespace InterCat.Application;

/// <summary>
/// How a group of processes is named under a grouping, in one set of words for the window and the command line (R5).
/// </summary>
public static class GroupingText
{
    /// <summary>The processes whose lifecycle records named no terminal session, as one group names them.</summary>
    public const string SessionNotRecorded = "Terminal session not recorded";

    /// <summary>
    /// The processes whose lifecycle records named no package they ran in - an observed none, not an unknown one - as their
    /// group names them (ADR-094).
    /// </summary>
    public const string NoPackage = "In no package";

    /// <summary>A terminal session's group: the processes whose lifecycle records name it.</summary>
    public static string Session(uint session) => string.Create(CultureInfo.InvariantCulture, $"Terminal session {session}");

    /// <summary>
    /// A package's group, by the full name its processes' lifecycle records name; the empty name is the group of those whose
    /// records named none.
    /// </summary>
    public static string Package(string package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Length == 0 ? NoPackage : package;
    }
}
