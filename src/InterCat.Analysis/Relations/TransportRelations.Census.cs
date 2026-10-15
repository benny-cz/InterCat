using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>
/// What became of one related mechanism's records under an evidence policy, each record counted once (§6.3, §6.8): in a
/// relationship the policy admits, in one it withholds, in a connection whose other end no record of the capture holds -
/// one to another host, or one on this machine whose other end went unseen - in a connection that could not be paired
/// with one process at each end, or naming no complete endpoint. It is how the graph says why it draws the relationships
/// it draws, and why it draws none, rather than leaving an empty pane to be read as a failure.
/// </summary>
/// <param name="Related">Records of the relationships <see cref="Relationships"/> counts.</param>
/// <param name="Relationships">Paired connection incarnations the policy admits, each one relationship.</param>
/// <param name="Withheld">Records of the relationships <see cref="WithheldRelationships"/> counts.</param>
/// <param name="WithheldRelationships">Paired connection incarnations the policy withholds, held only as candidates.</param>
/// <param name="ToOtherHosts">Records of the connections <see cref="OtherHostConnections"/> counts.</param>
/// <param name="OtherHostConnections">
/// Connections held by one process whose other end is an address that is neither loopback nor the connection's own,
/// which no record of the capture holds: most often another host.
/// </param>
/// <param name="OtherHosts">The distinct addresses those connections ran to.</param>
/// <param name="ToUnseenLocalEnds">Records of the connections <see cref="UnseenLocalConnections"/> counts.</param>
/// <param name="UnseenLocalConnections">
/// Connections held by one process whose other end is on this machine - the connection's own address, or another of
/// IPv4's loopback network - and which no record of the capture holds.
/// </param>
/// <param name="Unpaired">
/// Records of connections no relationship or one-sided connection holds: whose partner at the other end could be either
/// of several, or whose records name no single process at an end.
/// </param>
/// <param name="WithoutEndpoints">Records naming no complete pair of endpoints, whose other end cannot be looked for.</param>
public sealed record TransportCensus(
    Mechanism Mechanism,
    long Related,
    int Relationships,
    long Withheld,
    int WithheldRelationships,
    long ToOtherHosts,
    int OtherHostConnections,
    int OtherHosts,
    long ToUnseenLocalEnds,
    int UnseenLocalConnections,
    long Unpaired,
    long WithoutEndpoints)
{
    /// <summary>Every record of the mechanism the relations were derived from.</summary>
    public long Records => Related + Withheld + ToOtherHosts + ToUnseenLocalEnds + Unpaired + WithoutEndpoints;

    /// <summary>A census of a mechanism no record of which was seen.</summary>
    public static TransportCensus None(Mechanism mechanism) => new(mechanism, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// One process instance's connections to other hosts over one mechanism (§6.3's unresolved resources): the one-sided
/// connections a census counts as to another host, held by that instance. Who is at their other ends is not observed (P7).
/// </summary>
/// <param name="Mechanism">The transport the connections ran over.</param>
/// <param name="Holder">The instance's position in the process index the relations were derived with.</param>
/// <param name="Connections">Its connections to other hosts.</param>
/// <param name="Hosts">The distinct addresses they ran to.</param>
/// <param name="Records">The records they hold.</param>
/// <param name="Strength">How strongly their records bind to the instance: the weakest of them.</param>
public sealed record HostReach(Mechanism Mechanism, int Holder, int Connections, int Hosts, long Records, RelationStrength Strength);

public sealed partial class TransportRelationIndex
{
    /// <summary>
    /// Each instance's connections to other hosts over <paramref name="mechanism"/>, in holder order: exactly the
    /// connections <see cref="Census"/> counts as to another host, before any evidence policy. It reads no row.
    /// </summary>
    public IReadOnlyList<HostReach> ReachOfOtherHosts(Mechanism mechanism)
    {
        if (!Relates(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism,
                "This rule relates no record of that mechanism, so it reaches no host.");
        }

        var reach = new SortedDictionary<int, (int Connections, HashSet<(byte, UInt128)> Hosts, long Records, RelationStrength Weakest)>();
        foreach ((EndKey key, EndTimeline timeline) in ends)
        {
            if (key.Protocol != (byte)mechanism || OnThisMachine(key))
            {
                continue;
            }

            foreach (Incarnation incarnation in timeline.Incarnations)
            {
                if (incarnation is not { Records: > 0, Pairing: Pairing.NotObserved, Channel: >= 0, Holder: >= 0, Ambiguous: false })
                {
                    continue;
                }

                if (!reach.TryGetValue(incarnation.Holder, out var held))
                {
                    held = (0, [], 0, RelationStrength.Direct);
                }

                _ = held.Hosts.Add((key.Family, key.RemoteAddress));
                reach[incarnation.Holder] = (held.Connections + 1, held.Hosts, held.Records + incarnation.Records,
                    Weaker(held.Weakest, incarnation.Weakest));
            }
        }

        return [.. reach.Select(entry => new HostReach(
            mechanism, entry.Key, entry.Value.Connections, entry.Value.Hosts.Count, entry.Value.Records, entry.Value.Weakest))];
    }

    /// <summary>
    /// What became of <paramref name="mechanism"/>'s records under <paramref name="policy"/>. It reads no row: every record
    /// is in exactly one incarnation of one end, or among the records that name no end, and each incarnation knows how it
    /// was paired and who holds it. A relationship is counted once, from the end whose endpoint sorts first, as
    /// <see cref="Relations"/> lists it, and its records from both of its ends.
    /// </summary>
    public TransportCensus Census(Mechanism mechanism, EvidencePolicy policy)
    {
        if (!Relates(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism,
                "This rule relates no record of that mechanism, so it has no census.");
        }

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        long related = 0;
        long withheld = 0;
        long toOtherHosts = 0;
        long toLocalEnds = 0;
        long unpaired = 0;
        int relationships = 0;
        int withheldRelationships = 0;
        int otherHostConnections = 0;
        int localConnections = 0;
        var otherHosts = new HashSet<(byte Family, UInt128 Address)>();
        foreach ((EndKey key, EndTimeline timeline) in ends)
        {
            if (key.Protocol != (byte)mechanism)
            {
                continue;
            }

            bool counts = key.CompareTo(key.Mirror()) <= 0;
            foreach (Incarnation incarnation in timeline.Incarnations)
            {
                if (incarnation.Records == 0)
                {
                    continue;
                }

                bool held = incarnation is { Holder: >= 0, Ambiguous: false };
                if (incarnation is { Pairing: Pairing.Paired, Partner: { Holder: >= 0, Ambiguous: false } other } && held)
                {
                    // A relationship is as strong as the weakest binding it rests on, at either end (BuildRelations).
                    RelationStrength strength = Weaker(Weaker(RelationStrength.Correlated, incarnation.Weakest), other.Weakest);
                    bool admitted = new ProcessBinding(incarnation.Holder, strength, ProcessBindingReason.Bound).IsAdmittedUnder(policy);
                    if (admitted)
                    {
                        related += incarnation.Records;
                        relationships += counts ? 1 : 0;
                    }
                    else
                    {
                        withheld += incarnation.Records;
                        withheldRelationships += counts ? 1 : 0;
                    }

                    continue;
                }

                if (incarnation is { Pairing: Pairing.NotObserved, Channel: >= 0 } && held)
                {
                    if (OnThisMachine(key))
                    {
                        toLocalEnds += incarnation.Records;
                        localConnections++;
                    }
                    else
                    {
                        toOtherHosts += incarnation.Records;
                        otherHostConnections++;
                        _ = otherHosts.Add((key.Family, key.RemoteAddress));
                    }

                    continue;
                }

                unpaired += incarnation.Records;
            }
        }

        return new(
            mechanism,
            related,
            relationships,
            withheld,
            withheldRelationships,
            toOtherHosts,
            otherHostConnections,
            otherHosts.Count,
            toLocalEnds,
            localConnections,
            unpaired,
            withoutEnd.GetValueOrDefault(mechanism));
    }

    /// <summary>
    /// Whether an end's other end is on this machine: at the end's own address, as every connection over ::1 is, or at
    /// another address of IPv4's loopback network, 127.0.0.0/8, whether named as IPv4 or as an IPv4-mapped IPv6 address.
    /// </summary>
    private static bool OnThisMachine(EndKey key)
    {
        if (key.RemoteAddress == key.LocalAddress)
        {
            return true;
        }

        if (key.Family != 6)
        {
            return ((uint)key.RemoteAddress >> 24) == 127;
        }

        const ulong MappedPrefix = 0x0000_FFFF_0000_0000;
        return (ulong)(key.RemoteAddress >> 64) == 0
            && ((ulong)key.RemoteAddress & 0xFFFF_FFFF_0000_0000) == MappedPrefix
            && (((uint)key.RemoteAddress) >> 24) == 127;
    }
}
