using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Processes' connections to other hosts (<see cref="HostLink"/>) in a person's words, where the window's graph and icat
/// overview say them: TCP connections and UDP flows, as a process's rung lists each one.
/// </summary>
public static class HostLinkText
{
    /// <summary>A transport's connections counted: "1 TCP connection", "5 UDP flows".</summary>
    public static string Connections(Mechanism mechanism, long count) =>
        CountText.Of(count, mechanism == Mechanism.Udp ? "UDP flow" : "TCP connection");

    /// <summary>One process's connections over one transport: "42 TCP connections to 17 hosts", "5 UDP flows with 3 hosts".</summary>
    public static string Reach(HostLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Connections(link.Mechanism, link.Connections)
            + (link.Mechanism == Mechanism.Udp ? " with " : " to ") + CountText.Of(link.Hosts, "host");
    }

    /// <summary>
    /// Several processes' connections over one transport: "120 TCP connections from 12 processes to 40 hosts", "5 UDP
    /// flows from 2 processes with 3 hosts". A host two of them reached is one host.
    /// </summary>
    public static string Reach(Mechanism mechanism, IReadOnlyCollection<HostLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        int hosts = links.SelectMany(link => link.Addresses).Select(address => address.Address)
            .Distinct(StringComparer.Ordinal).Count();
        return Connections(mechanism, links.Sum(link => (long)link.Connections)) + " from "
            + CountText.Of(links.Select(link => link.Process).Distinct().Count(), "process", "processes")
            + (mechanism == Mechanism.Udp ? " with " : " to ") + CountText.Of(hosts, "host");
    }

    /// <summary>
    /// What reached one other host, transport by transport, and from how many processes: "12 TCP connections from 2
    /// processes", "2 TCP connections and 3 UDP flows from 1 process". Where every host named is one process's,
    /// <paramref name="countsProcesses"/> false leaves the process out: "2 TCP connections and 3 UDP flows".
    /// </summary>
    public static string Reached(OtherHost host, bool countsProcesses = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        string[] transports = [.. new[]
        {
            host.TcpConnections > 0 ? Connections(Mechanism.Tcp, host.TcpConnections) : null,
            host.UdpFlows > 0 ? Connections(Mechanism.Udp, host.UdpFlows) : null,
        }.OfType<string>()];
        return string.Join(" and ", transports)
            + (countsProcesses ? " from " + CountText.Of(host.Processes, "process", "processes") : string.Empty);
    }

    /// <summary>
    /// Hosts named, the busiest first, at most <paramref name="named"/> of them: "Hosts: 142.250.1.1 and 20.42.65.1", or
    /// where there are more, "Busiest of 17 hosts: 142.250.1.1, 20.42.65.1 and 8.8.8.8". Null when there are none.
    /// </summary>
    public static string? Named(IReadOnlyList<OtherHost> hosts, int named)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(named);
        if (hosts.Count == 0)
        {
            return null;
        }

        string listed = WorkspaceLayout.Series([.. hosts.Take(named).Select(host => host.Address)]);
        return hosts.Count <= named
            ? CountText.Agree(hosts.Count, "Host: ", "Hosts: ") + listed
            : string.Create(CultureInfo.CurrentCulture, $"Busiest of {hosts.Count:N0} hosts: ") + listed;
    }

    /// <summary>
    /// A set of processes' connections to other hosts, transport by transport: one process's with the hosts they reached,
    /// several processes' with how many of them made them. Empty when none has any.
    /// </summary>
    public static IReadOnlyList<string> Parts(IEnumerable<HostLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        HostLink[] all = [.. links];
        bool one = all.Select(link => link.Process).Distinct().Count() == 1;
        return [.. all
            .GroupBy(link => link.Mechanism)
            .OrderBy(transport => transport.Key)
            .Select(transport => one ? Reach(transport.First()) : Reach(transport.Key, [.. transport]))];
    }

    /// <summary>The same on one line, its transports apart: "42 TCP connections to 17 hosts · 5 UDP flows with 3 hosts".</summary>
    public static string Line(IEnumerable<HostLink> links) => string.Join(" · ", Parts(links));
}
