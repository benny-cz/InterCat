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
    /// The processes whose connections ran to each address <paramref name="links"/> reached, made or accepted, the busiest
    /// there first by the records their connections to it hold, then by instance: one that reached it over both transports
    /// once, its records summed. Who is at the address is not observed (P7).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(ProcessInstanceId Process, long Records)>> Reachers(
        IEnumerable<HostLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return links
            .SelectMany(link => link.Addresses.Select(address => (link.Process, address.Address, address.Records)))
            .GroupBy(reach => reach.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                host => host.Key,
                host => (IReadOnlyList<(ProcessInstanceId Process, long Records)>)[.. host
                    .GroupBy(reach => reach.Process)
                    .Select(process => (Process: process.Key, Records: process.Sum(reach => reach.Records)))
                    .OrderByDescending(process => process.Records)
                    .ThenBy(process => process.Process.ToString(), StringComparer.Ordinal)],
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The processes that reached a host, as a narrow row names them and an RPC channel's row names who served it: the
    /// busiest, "updater.exe · PID 310", and how many more there are, "browser.exe · PID 300 and 4 more". Empty for none.
    /// </summary>
    public static string ReachedBy(IReadOnlyList<(ProcessInstanceId Process, long Records)> reachers,
        Func<ProcessInstanceId, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(reachers);
        ArgumentNullException.ThrowIfNull(nameOf);
        return reachers.Count == 0 ? string.Empty
            : nameOf(reachers[0].Process)
                + (reachers.Count > 1 ? string.Create(CultureInfo.CurrentCulture, $" and {reachers.Count - 1:N0} more") : string.Empty);
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
    /// The connections to a host the processes accepted from it, with their own ports the connections arrived at:
    /// "accepted here at ports 443 and 8443" where every connection to it was, else "1 accepted here at port 3389". Empty
    /// where the capture saw none accepted.
    /// </summary>
    public static string Accepted(OtherHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Accepted == 0)
        {
            return string.Empty;
        }

        string at = " here at " + Ports(host.AcceptedPorts);
        return host.Accepted == host.TcpConnections + host.UdpFlows
            ? "accepted" + at
            : string.Create(CultureInfo.CurrentCulture, $"{host.Accepted:N0} accepted{at}");
    }

    /// <summary>
    /// The connections a process accepted from other hosts, with its own ports they arrived at: "accepted here at port 445"
    /// where every one of its TCP connections to them was, else "1 accepted here at port 3389". Empty where the capture saw
    /// none accepted.
    /// </summary>
    public static string Accepted(IReadOnlyCollection<HostLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        int accepted = links.SelectMany(link => link.Addresses).Sum(address => address.Accepted);
        if (accepted == 0)
        {
            return string.Empty;
        }

        ushort[] ports = [.. links.SelectMany(link => link.Addresses).SelectMany(address => address.AcceptedPorts).Distinct().Order()];
        string at = ports.Length == 0 ? " here" : " here at " + Ports(ports);
        long tcp = links.Where(link => link.Mechanism == Mechanism.Tcp).Sum(link => (long)link.Connections);
        return accepted == tcp ? "accepted" + at : string.Create(CultureInfo.CurrentCulture, $"{accepted:N0} accepted{at}");
    }

    /// <summary>
    /// The same as a table's cell states it, the ports as numbers: "2 at 443 and 8443"; "-" where none was accepted.
    /// </summary>
    public static string AcceptedCell(int accepted, IReadOnlyList<ushort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return accepted == 0 ? "-" : string.Create(CultureInfo.CurrentCulture, $"{accepted:N0} at ") + PortNumbers(ports);
    }

    /// <summary>The most ports <see cref="Ports"/> names before it counts the rest.</summary>
    public const int PortsNamed = 3;

    /// <summary>
    /// The ports at an address connections ran to, the lowest first: "port 443", "ports 80 and 443", or past
    /// <see cref="PortsNamed"/> of them, "ports 80, 443, 8080 and 2 more". A port is where they ran at that address, never
    /// what it serves. Empty when none is known.
    /// </summary>
    public static string Ports(IReadOnlyList<ushort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return ports.Count == 0 ? string.Empty : (ports.Count == 1 ? "port " : "ports ") + PortNumbers(ports);
    }

    /// <summary>The same ports as a table's cell states them, numbers alone: "443", "80 and 443", "80, 443, 8080 and 2 more".</summary>
    public static string PortNumbers(IReadOnlyList<ushort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        string[] named = [.. ports.Take(PortsNamed).Select(port => port.ToString(CultureInfo.InvariantCulture))];
        return ports.Count <= PortsNamed
            ? WorkspaceLayout.Series(named)
            : string.Join(", ", named) + string.Create(CultureInfo.CurrentCulture, $" and {ports.Count - PortsNamed:N0} more");
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
