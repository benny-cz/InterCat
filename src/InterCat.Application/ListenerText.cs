using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Who connected to a listener (<see cref="PortListener"/>), as `icat ports` and the window's list of listening ports say
/// it (§6.3): each kind of end, with the connections it made there - the processes of the capture, one named, several
/// counted with the programs they ran; other hosts, one by its address, several counted; and processes on this machine the
/// capture did not observe - whose connections add up to those the listener accepted.
/// </summary>
public static class ListenerText
{
    /// <summary>The most programs a listener's words name of those whose processes connected to it; past it, they are counted.</summary>
    public const int ProgramsNamed = 3;

    /// <summary>
    /// Each kind of end that connected to <paramref name="listener"/>, with its connections, in the order the processes of
    /// the capture, other hosts, then processes the capture did not observe: (34, "34 processes of chrome.exe"), (1,
    /// "10.0.0.6"), (2, "unobserved processes on this machine"). A process that connected to itself is "(itself)".
    /// <paramref name="named"/> names a process, <paramref name="program"/> the program it ran.
    /// </summary>
    public static IReadOnlyList<(long Connections, string Who)> From(PortListener listener, Func<ProcessInstance, string> named,
        Func<ProcessInstance, string> program)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(program);
        var parts = new List<(long Connections, string Who)>();
        if (listener.Connectors is [var only])
        {
            parts.Add((listener.FromProcesses, named(only.Process)
                + (only.Process.Id == listener.Process.Id ? " (itself)" : string.Empty)));
        }
        else if (listener.Connectors.Count > 1)
        {
            parts.Add((listener.FromProcesses, string.Create(CultureInfo.CurrentCulture, $"{listener.Connectors.Count:N0} processes of ")
                + Programs(listener.Connectors, program)));
        }

        if (listener.Hosts is [var host])
        {
            parts.Add((listener.FromHosts, host.Address));
        }
        else if (listener.Hosts.Count > 1)
        {
            parts.Add((listener.FromHosts, CountText.Of(listener.Hosts.Count, "host")));
        }

        if (listener.FromUnseenLocalEnds > 0)
        {
            parts.Add((listener.FromUnseenLocalEnds, "unobserved processes on this machine"));
        }

        return parts;
    }

    /// <summary>
    /// The programs the processes that connected ran, the most connections first, then by name: named where there are
    /// few - "chrome.exe", "svchost.exe and services.exe" - else counted, "7 programs".
    /// </summary>
    private static string Programs(IReadOnlyList<PortConnector> connectors, Func<ProcessInstance, string> program)
    {
        string[] named = [.. connectors
            .GroupBy(connector => program(connector.Process), StringComparer.Ordinal)
            .OrderByDescending(ran => ran.Sum(connector => connector.Connections))
            .ThenBy(ran => ran.Key, StringComparer.OrdinalIgnoreCase)
            .Select(ran => ran.Key)];
        return named.Length <= ProgramsNamed ? WorkspaceLayout.Series(named) : CountText.Of(named.Length, "program");
    }
}
