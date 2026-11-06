using System.Globalization;
using InterCat.Analysis;

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

    /// <summary>
    /// A service container's group: its host's PID and image name, and the services it hosted, the first named and the rest
    /// counted - the host's records, never one service's (ADR-096).
    /// </summary>
    public static string Container(int processId, string? imageName, IReadOnlyList<HostedService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        string host = imageName is { Length: > 0 } image
            ? string.Create(CultureInfo.InvariantCulture, $"PID {processId} {image}")
            : string.Create(CultureInfo.InvariantCulture, $"PID {processId}");
        string more = services.Count > ServiceText.Named
            ? string.Create(CultureInfo.CurrentCulture, $" and {services.Count - ServiceText.Named:N0} more")
            : string.Empty;
        return $"{host} hosting {string.Join(", ", services.Take(ServiceText.Named).Select(static service => service.Name))}{more}";
    }
}
