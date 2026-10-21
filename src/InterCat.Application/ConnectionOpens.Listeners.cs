using System.Net;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

public static partial class ConnectionOpens
{
    /// <summary>
    /// The ports this machine's process instances accepted TCP connections at under <paramref name="policy"/>, as
    /// <see cref="Count"/> counts their connections accepted over the whole session: one listener for each instance and port
    /// of its own its accepted connections arrived at, with how many and who connected - processes of the capture, other
    /// hosts by address, and ends on this machine no record holds. The busiest first, then by port and instance. It is the
    /// session's netstat of listeners: which process served at which port, and for whom.
    /// </summary>
    public static IReadOnlyList<PortListener> Listeners(TransportRelationIndex relations, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var listeners = new Dictionary<(ProcessInstanceId Process, ushort Port), ListenerTally>();
        foreach (TransportRelation relation in relations.Relations.Where(relation => Counted(relation, policy, null)))
        {
            if (AcceptingPort(relation) is { } port)
            {
                bool firstConnected = relation.Opening == RelationOpening.FirstConnected;
                TallyOf(firstConnected ? relation.Second : relation.First, port)
                    .FromProcess(firstConnected ? relation.First : relation.Second);
            }
        }

        foreach (TransportConnection connection in relations.OneSided.Where(connection =>
            connection.Opening == ConnectionOpening.Accepted && Counted(connection, policy, null)))
        {
            if (PortOf(connection.LocalEndpoint) is { } port)
            {
                TallyOf(connection.Holder, port).FromEnd(
                    HostScopeKeys.EndpointAddress(connection.LocalEndpoint), HostScopeKeys.EndpointAddress(connection.RemoteEndpoint));
            }
        }

        return [.. listeners.Values
            .Select(tally => tally.Listener())
            .OrderByDescending(listener => listener.Accepted)
            .ThenBy(listener => listener.Port)
            .ThenBy(listener => listener.Process.Id.ToString(), StringComparer.Ordinal)];

        ListenerTally TallyOf(ProcessInstance process, ushort port)
        {
            if (!listeners.TryGetValue((process.Id, port), out ListenerTally? tally))
            {
                listeners[(process.Id, port)] = tally = new ListenerTally(process, port);
            }

            return tally;
        }
    }

    /// <summary>
    /// Whether a one-sided connection's other end is on this machine, as the census tells an unseen local end from another
    /// host: at its holder's own address, or at loopback's - ::1, or IPv4's 127.0.0.0/8, an IPv4-mapped address by its IPv4
    /// address. Both are written as every surface writes an address, so one address is one text.
    /// </summary>
    private static bool OnThisMachine(string localAddress, string remoteAddress) =>
        string.Equals(localAddress, remoteAddress, StringComparison.Ordinal)
        || (IPAddress.TryParse(remoteAddress, out IPAddress? remote)
            && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote));

    /// <summary>One instance's connections accepted at one port of its own, and who connected, as they are counted.</summary>
    private sealed class ListenerTally(ProcessInstance process, ushort port)
    {
        private readonly Dictionary<ProcessInstanceId, (ProcessInstance Process, long Connections)> connectors = [];
        private readonly Dictionary<string, long> hosts = new(StringComparer.Ordinal);
        private long accepted;
        private long unseen;

        /// <summary>One connection a process of the capture made to it.</summary>
        public void FromProcess(ProcessInstance connector)
        {
            accepted++;
            connectors[connector.Id] = (connector, connectors.GetValueOrDefault(connector.Id).Connections + 1);
        }

        /// <summary>One connection from an end no record holds: another host's, or one on this machine.</summary>
        public void FromEnd(string localAddress, string remoteAddress)
        {
            accepted++;
            if (OnThisMachine(localAddress, remoteAddress))
            {
                unseen++;
            }
            else
            {
                hosts[remoteAddress] = hosts.GetValueOrDefault(remoteAddress) + 1;
            }
        }

        public PortListener Listener() => new(port, process, accepted)
        {
            Connectors = [.. connectors.Values
                .OrderByDescending(entry => entry.Connections)
                .ThenBy(entry => entry.Process.Id.ToString(), StringComparer.Ordinal)
                .Select(entry => new PortConnector(entry.Process, entry.Connections))],
            Hosts = [.. hosts
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new PortHost(entry.Key, entry.Value))],
            FromUnseenLocalEnds = unseen,
        };
    }
}

/// <summary>
/// One process instance's TCP connections accepted at one port of its own, as <see cref="ConnectionOpens"/> counts them:
/// <see cref="Accepted"/> of them, and who connected - the processes of the capture, the other hosts and the ends on this
/// machine no record holds, whose connections add up to it.
/// </summary>
public sealed record PortListener(ushort Port, ProcessInstance Process, long Accepted)
{
    /// <summary>The processes of the capture that connected, each once, the most connections first, then by instance.</summary>
    public IReadOnlyList<PortConnector> Connectors { get; init; } = [];

    /// <summary>The other hosts that connected, each by its address once, the most connections first, then by address.</summary>
    public IReadOnlyList<PortHost> Hosts { get; init; } = [];

    /// <summary>
    /// How many came from ends on this machine no record holds: a process at loopback, or at the listener's own address,
    /// that the capture did not observe.
    /// </summary>
    public long FromUnseenLocalEnds { get; init; }

    /// <summary>How many came from processes of the capture.</summary>
    public long FromProcesses => Connectors.Sum(connector => connector.Connections);

    /// <summary>How many came from other hosts.</summary>
    public long FromHosts => Hosts.Sum(host => host.Connections);
}

/// <summary>A process of the capture that connected to a listener's port, and how many of its connections it made there.</summary>
public sealed record PortConnector(ProcessInstance Process, long Connections);

/// <summary>Another host that connected to a listener's port, by its address, and how many of its connections came from it.</summary>
public sealed record PortHost(string Address, long Connections);
