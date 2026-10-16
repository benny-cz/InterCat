using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The transport relation index over random captures rather than chosen examples (R10, §13.6): what its census says became
/// of each mechanism's records is what its relationships and one-sided connections hold, under every evidence policy; each
/// process's reach of other hosts is its one-sided connections to other hosts, address by address; and none of it depends
/// on how the records were cut into segments. Every case comes from a printed seed.
/// </summary>
public sealed class RelationCensusProperties
{
    private const int Cases = 30;

    public static TheoryData<int> Seeds => [7, 542, 20_261_015];

    [Theory(DisplayName = "§6.8: a random capture's census counts every record once, as its relationships and connections hold them, under every policy")]
    [MemberData(nameof(Seeds))]
    public void ACensusIsWhatTheRelationsHold(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            string label = string.Create(CultureInfo.InvariantCulture, $"seed {seed}, case {index}");
            ObservationRowV1[] rows = Capture(random);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1));
            (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derive(session.Store);
            Holds(rows, processes, relations, label);

            // Cut into other segments, the same records relate alike.
            using var again = new TemporarySession();
            Publish(again.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1));
            (ProcessInstanceIndex otherProcesses, TransportRelationIndex other) = Derive(again.Store);
            Assert.Equal(processes.Instances, otherProcesses.Instances);
            AssertSame(relations, processes, other, otherProcesses, label);
        }
    }

    /// <summary>Every invariant one derivation must hold.</summary>
    private static void Holds(
        ObservationRowV1[] rows, ProcessInstanceIndex processes, TransportRelationIndex relations, string label)
    {
        foreach (Mechanism mechanism in new[] { Mechanism.Tcp, Mechanism.Udp })
        {
            long recorded = rows.Count(row => row.Mechanism == mechanism);
            TransportRelation[] related = [.. relations.Relations.Where(relation => relation.Mechanism == mechanism)];
            TransportConnection[] oneSided = [.. relations.OneSided.Where(connection => connection.Mechanism == mechanism)];
            TransportConnection[] toOtherHosts = [.. oneSided
                .Where(connection => !OnThisMachine(connection.LocalEndpoint, connection.RemoteEndpoint))];
            TransportCensus? first = null;
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                TransportCensus census = relations.Census(mechanism, policy);
                string at = $"{label}, {mechanism}, {policy}";

                // Every record once.
                Assert.True(census.Records == recorded, $"{at}: the census counts {census.Records} of {recorded} records");

                // Its relationships are the paired connections both of whose holders are decided: those the policy admits
                // are related and the rest withheld, each with the records at its two ends.
                TransportRelation[] admitted = [.. related.Where(relation => Admitted(relation.Strength, policy))];
                Assert.True(
                    (admitted.Length, related.Length - admitted.Length) == (census.Relationships, census.WithheldRelationships),
                    $"{at}: {census.Relationships} related and {census.WithheldRelationships} withheld of {related.Length}");
                long admittedRecords = admitted.Sum(relation => relation.Records);
                Assert.True(
                    (admittedRecords, related.Sum(relation => relation.Records) - admittedRecords) == (census.Related, census.Withheld),
                    $"{at}: {census.Related} related and {census.Withheld} withheld records");

                // Its one-sided connections are those to other hosts and those to unseen ends on this machine.
                long otherHostRecords = toOtherHosts.Sum(connection => connection.Records);
                Assert.True(
                    (toOtherHosts.Length, oneSided.Length - toOtherHosts.Length, otherHostRecords,
                        oneSided.Sum(connection => connection.Records) - otherHostRecords)
                    == (census.OtherHostConnections, census.UnseenLocalConnections, census.ToOtherHosts, census.ToUnseenLocalEnds),
                    $"{at}: one-sided connections counted apart from the census");
                Assert.True(
                    toOtherHosts.Select(connection => Address(connection.RemoteEndpoint)).Distinct(StringComparer.Ordinal).Count()
                        == census.OtherHosts,
                    $"{at}: {census.OtherHosts} other hosts");

                // Records with no admitted other end are at least those with no other end at all, and at most every record
                // outside the related ones: a record's other end rests on the evidence at that end alone (relations-v1 §4),
                // so where only its own end binds weakly, a withheld relationship's record has an admitted other end.
                long withoutPeer = relations.RecordsWithoutAdmittedPeer(mechanism, policy);
                Assert.True(
                    withoutPeer >= census.ToOtherHosts + census.ToUnseenLocalEnds + census.WithoutEndpoints
                        && withoutPeer <= census.Records - census.Related,
                    $"{at}: {withoutPeer} records without an admitted peer; {census}");

                // A policy moves relationships between related and withheld, and nothing else.
                first ??= census;
                Assert.Equal(Unrelated(first), Unrelated(census));
                Assert.Equal(
                    (first.Related + first.Withheld, first.Relationships + first.WithheldRelationships),
                    (census.Related + census.Withheld, census.Relationships + census.WithheldRelationships));
            }

            // A connection the capture saw open was opened by a connect or an accept, and one it did not by neither; a flow
            // never was.
            Assert.All(toOtherHosts, connection => Assert.Equal(
                connection.OpenWitnessed, connection.Opening != ConnectionOpening.NotWitnessed));
            Assert.All(toOtherHosts.Where(connection => connection.Mechanism == Mechanism.Udp),
                connection => Assert.Equal(ConnectionOpening.NotWitnessed, connection.Opening));

            // Each process's reach of other hosts is its one-sided connections to other hosts, address by address, with the
            // ports at each they ran to and, apart, how many it connected and accepted and its own ports the accepted ones
            // arrived at: the most records first, then the most connections, then by address; as weak as the weakest of them.
            IReadOnlyList<HostReach> reach = relations.ReachOfOtherHosts(mechanism);
            Assert.Equal(reach.Select(entry => entry.Holder).Order(), reach.Select(entry => entry.Holder));
            Assert.Equal(reach.Count, reach.Select(entry => entry.Holder).Distinct().Count());
            Assert.All(reach, entry => Assert.All(entry.Addresses, address =>
                Assert.True(address.Connections >= 1 && address.Records >= address.Connections, $"{label}: {address}")));
            Assert.Equal(
                toOtherHosts
                    .GroupBy(connection => connection.Holder.Id)
                    .Select(holder => (holder.Key.ToString(), Addresses(holder
                        .GroupBy(connection => Address(connection.RemoteEndpoint), StringComparer.Ordinal)
                        .Select(address => new HostAddress(address.Key, address.Count(), address.Sum(connection => connection.Records))
                        {
                            Ports = [.. address.Where(connection => connection.Opening != ConnectionOpening.Accepted)
                                .Select(connection => Port(connection.RemoteEndpoint)).Distinct().Order()],
                            Connected = address.Count(connection => connection.Opening == ConnectionOpening.Connected),
                            Accepted = address.Count(connection => connection.Opening == ConnectionOpening.Accepted),
                            AcceptedPorts = [.. address.Where(connection => connection.Opening == ConnectionOpening.Accepted)
                                .Select(connection => Port(connection.LocalEndpoint)).Distinct().Order()],
                        })
                        .OrderByDescending(address => address.Records)
                        .ThenByDescending(address => address.Connections)
                        .ThenBy(address => address.Address, StringComparer.Ordinal)),
                        holder.Max(connection => connection.Strength)))
                    .Order(),
                reach.Select(entry => (processes.Instances[entry.Holder].Id.ToString(), Addresses(entry.Addresses), entry.Strength))
                    .Order());
        }
    }

    /// <summary>Two derivations of the same records alike in every answer they give.</summary>
    private static void AssertSame(
        TransportRelationIndex expected, ProcessInstanceIndex expectedProcesses, TransportRelationIndex actual,
        ProcessInstanceIndex actualProcesses, string label)
    {
        Assert.Equal(expected.Relations, actual.Relations);
        Assert.Equal(expected.OneSided, actual.OneSided);
        Assert.Equal(expected.Channels, actual.Channels);
        foreach (Mechanism mechanism in new[] { Mechanism.Tcp, Mechanism.Udp })
        {
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                Assert.True(expected.Census(mechanism, policy) == actual.Census(mechanism, policy), $"{label}: {mechanism} census");
                Assert.Equal(expected.RecordsWithoutAdmittedPeer(mechanism, policy), actual.RecordsWithoutAdmittedPeer(mechanism, policy));
            }

            Assert.Equal(Reach(expected, expectedProcesses, mechanism), Reach(actual, actualProcesses, mechanism));
        }
    }

    /// <summary>A mechanism's reach of other hosts, holder by holder, each by its instance and its addresses in order.</summary>
    internal static IEnumerable<(string Holder, string Addresses, RelationStrength Strength)> Reach(
        TransportRelationIndex relations, ProcessInstanceIndex processes, Mechanism mechanism) =>
        relations.ReachOfOtherHosts(mechanism)
            .Select(entry => (processes.Instances[entry.Holder].Id.ToString(), Addresses(entry.Addresses), entry.Strength));

    private static string Addresses(IEnumerable<HostAddress> addresses) =>
        string.Join(" ", addresses.Select(address => string.Create(CultureInfo.InvariantCulture,
            $"{address.Address}={address.Connections}/{address.Records}@{string.Join(',', address.Ports)}"
            + $"+{address.Connected}/{address.Accepted}@{string.Join(',', address.AcceptedPorts)}")));

    /// <summary>A census with its relationships left out: what no evidence policy may change.</summary>
    private static TransportCensus Unrelated(TransportCensus census) =>
        census with { Related = 0, Relationships = 0, Withheld = 0, WithheldRelationships = 0 };

    private static bool Admitted(RelationStrength strength, EvidencePolicy policy) =>
        new ProcessBinding(0, strength, ProcessBindingReason.Bound).IsAdmittedUnder(policy);

    /// <summary>
    /// Whether a one-sided end's other end is on this machine, as `relations-v1` §5c defines it: at the end's own address,
    /// or in IPv4's loopback network, named as IPv4 or IPv4-mapped IPv6.
    /// </summary>
    private static bool OnThisMachine(string local, string remote)
    {
        string far = Address(remote);
        return far == Address(local)
            || (!remote.StartsWith('[') && far.StartsWith("127.", StringComparison.Ordinal))
            || far.StartsWith("::ffff:127.", StringComparison.Ordinal);
    }

    /// <summary>An endpoint's port: the number after its last colon.</summary>
    private static ushort Port(string endpoint) =>
        ushort.Parse(endpoint.AsSpan(endpoint.LastIndexOf(':') + 1), NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>An endpoint's address: "a.b.c.d" of "a.b.c.d:port", the address inside the brackets of "[v6]:port".</summary>
    private static string Address(string endpoint) => endpoint.StartsWith('[')
        ? endpoint[1..endpoint.IndexOf(']', StringComparison.Ordinal)]
        : endpoint[..endpoint.LastIndexOf(':')];

    private static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Derive(SessionStore store)
    {
        IReadOnlyList<SegmentReaderV1> segments = Segments(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock);
        return (processes, TransportRelationIndex.Derive(segments, processes));
    }

    /// <summary>
    /// A random capture: processes created, exiting and reused, and connections between them over loopback, to unseen ends
    /// on this machine and to other hosts, IPv4, IPv6 and IPv4-mapped, TCP and UDP. A connection's records name its owner
    /// mostly, sometimes no owner and sometimes another process, and TCP connections are now and then opened, accepted and
    /// closed among their transfers, so a port is reused by a later incarnation; a few records name no endpoint at all.
    /// </summary>
    private static ObservationRowV1[] Capture(Random random)
    {
        int[] pids = [100, 200, 300, 400, 500];
        string[] external = ["142.250.1.1:443", "142.250.1.2:443", "20.42.65.1:443", "8.8.8.8:53"];
        string[] external6 = ["[2001:db8:1::1]:443", "[2001:db8:1::2]:443", "[::ffff:142.250.1.1]:443"];
        var events = new List<(long Ticks, Func<ulong, ObservationRowV1> Row)>();

        // Processes: most created at the start, some exiting and their PIDs reused, the rest seen only in their records.
        foreach (int pid in pids.Where(_ => random.Next(4) > 0))
        {
            events.Add((random.Next(0, 5), ordinal => Lifecycle(0, ObservationKind.Create, pid, ordinal)));
            if (random.Next(4) == 0)
            {
                long exit = random.Next(20, 300);
                events.Add((exit, ordinal => Lifecycle(exit, ObservationKind.Exit, pid, ordinal)));
                events.Add((exit + random.Next(1, 50), ordinal => Lifecycle(exit + 1, ObservationKind.Create, pid, ordinal)));
            }
        }

        int connections = random.Next(1, 30);
        for (int index = 0; index < connections; index++)
        {
            Mechanism mechanism = random.Next(4) == 0 ? Mechanism.Udp : Mechanism.Tcp;
            int port = 50_000 + random.Next(0, 12);
            (string Near, string Far, bool BothSides) ends = random.Next(10) switch
            {
                < 4 => ($"192.168.1.10:{port}", external[random.Next(external.Length)], false),
                4 => random.Next(3) == 0
                    ? ($"[::ffff:192.168.1.10]:{port}", external6[2], false)
                    : ($"[2001:db8::10]:{port}", external6[random.Next(2)], false),
                < 8 => ($"127.0.0.1:{port}", random.Next(2) == 0 ? "127.0.0.1:8080" : "127.0.0.1:9090", true),
                8 => ($"[::1]:{port}", "[::1]:8080", true),
                _ => random.Next(2) == 0 ? ($"127.0.0.1:{port}", "127.0.0.1:9999", false)
                    : ($"192.168.1.10:{port}", "192.168.1.10:7000", false),
            };
            int nearOwner = pids[random.Next(pids.Length)];
            int farOwner = pids[random.Next(pids.Length)];
            long ticks = random.Next(5, 400);
            int records = random.Next(1, 7);
            for (int record = 0; record < records; record++)
            {
                ticks += random.Next(0, 30);
                bool far = ends.BothSides && random.Next(2) == 0;
                string own = far ? ends.Far : ends.Near;
                string other = far ? ends.Near : ends.Far;
                int? owner = random.Next(15) == 0 ? null : random.Next(10) == 0 ? pids[random.Next(pids.Length)]
                    : far ? farOwner : nearOwner;
                ObservationKind kind = mechanism == Mechanism.Tcp && random.Next(6) == 0
                    ? (random.Next(3) switch { 0 => ObservationKind.Connect, 1 => ObservationKind.Accept, _ => ObservationKind.Disconnect })
                    : random.Next(2) == 0 ? ObservationKind.Send : ObservationKind.Receive;
                bool named = random.Next(25) > 0;
                long at = ticks;
                events.Add((at, ordinal =>
                {
                    ObservationRowV1 row = Transfer(at, kind,
                        kind == ObservationKind.Send ? AccountingSide.SendSide
                            : kind == ObservationKind.Receive ? AccountingSide.ReceiveSide : AccountingSide.EndpointActivity,
                        kind is ObservationKind.Send or ObservationKind.Receive ? 16 : 0, owner, ordinal) with { Mechanism = mechanism };

                    // A UDP receive names the datagram's sender first; every other record its own endpoint (ADR-019).
                    return !named ? row
                        : mechanism == Mechanism.Udp && kind == ObservationKind.Receive ? row.Between(other, own)
                        : row.Between(own, other);
                }));
            }
        }

        ulong next = 0;
        return [.. events
            .Select((entry, index) => (entry.Ticks, index, entry.Row))
            .OrderBy(entry => entry.Ticks)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.Row(++next) with { NativeTicks = entry.Ticks, SessionRelativeTicks = entry.Ticks * 100 })];
    }
}
