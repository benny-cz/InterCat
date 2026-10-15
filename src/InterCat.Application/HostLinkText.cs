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
    /// Several processes' connections over one transport: "120 TCP connections from 12 processes". Their hosts are not
    /// added up, since two processes may have reached the same one.
    /// </summary>
    public static string Reach(Mechanism mechanism, IReadOnlyCollection<HostLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return Connections(mechanism, links.Sum(link => (long)link.Connections)) + " from "
            + CountText.Of(links.Select(link => link.Process).Distinct().Count(), "process", "processes");
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
