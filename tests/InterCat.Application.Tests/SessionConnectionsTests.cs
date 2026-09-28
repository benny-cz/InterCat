using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A process's one-sided connections (§7.1): the TCP connections and UDP flows it held whose other end no record of the
/// capture holds, most often another host's, each with its records and the transport bytes they measured.
/// </summary>
public sealed class SessionConnectionsTests
{
    private const string Client = "127.0.0.1:50000";
    private const string Server = "127.0.0.1:8080";

    [Fact(DisplayName = "P7: a process's one-sided connections are its channels no record's other end holds, named by their endpoints, never by a guessed peer")]
    public void OneSidedConnectionsAreChannelsOfTheirHolder()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        (ProcessInstanceId client, ProcessInstanceId server) = Instances(session.Store);

        // The paired channel between processes 100 and 200 is not one-sided at either end.
        ConnectionList list = SessionConnections.OneSided(session.Store, client);
        Assert.Equal(["TCP to 10.0.0.9:443", "UDP to 8.8.8.8:53"], list.Connections.Select(connection => connection.Name));
        Assert.Empty(SessionConnections.OneSided(session.Store, server).Connections);

        ConnectionSummary tcp = list.Connections[0];
        Assert.True(TransportConnection.IsKey(tcp.Key));
        Assert.Equal(("192.168.1.5:52000", 5L), (tcp.LocalEndpoint, tcp.Records));
        Assert.Equal((1L, 100L, 0L, 2L, 200L, 1L),
            (tcp.Sends, tcp.SentBytes, tcp.UnmeasuredSends, tcp.Receives, tcp.ReceivedBytes, tcp.UnmeasuredReceives));
        Assert.Equal("opened and closed in the capture", tcp.Lifetime);
        Assert.Equal("100 B sent, 200 B received; 1 transfer of no stated size", tcp.Transfers(CultureInfo.InvariantCulture));
        Assert.Equal(new RankedValue(RankingMetric.BytesReceived, 200, 1, 1), tcp.RankedBy(RankingMetric.BytesReceived));
        Assert.Null(tcp.RankedBy(RankingMetric.Records));
        Assert.Equal("a datagram flow", list.Connections[1].Lifetime);

        // Its key reads exactly its records, and an interval that holds none of them leaves it out.
        SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, channelKey: tcp.Key);
        Assert.Equal([1_000L, 1_001L, 1_002L, 1_003L, 1_004L], records.Records.Select(record => record.Observation.NativeTicks));
        Assert.Equal(["UDP to 8.8.8.8:53"],
            SessionConnections.OneSided(session.Store, client, new TimeRange(1_900, 2_100)).Connections.Select(connection => connection.Name));
    }

    private static (ProcessInstanceId Client, ProcessInstanceId Server) Instances(SessionStore store)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store);
        return (overview.Nodes.Single(node => node.ProcessId == 100).Id, overview.Nodes.Single(node => node.ProcessId == 200).Id);
    }

    /// <summary>
    /// Process 100 connects to 10.0.0.9:443, sends 100 bytes, receives 200 and a transfer of no stated size, and
    /// disconnects; it sends a datagram to 8.8.8.8:53; and it holds a paired connection with process 200.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        const string local = "192.168.1.5:52000";
        const string remote = "10.0.0.9:443";
        return
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
            Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10).Between(Client, Server)),
            Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11).Between(Server, Client)),
            Timed(Transfer(1_000, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between(local, remote)),
            Timed(Transfer(1_001, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 21).Between(local, remote)),
            Timed(Transfer(1_002, ObservationKind.Receive, AccountingSide.ReceiveSide, 200, 100, 22).Between(local, remote)),
            Timed(Transfer(1_003, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 100, 23).Between(local, remote)),
            Timed(Transfer(1_004, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 24).Between(local, remote)),
            Timed(Transfer(2_000, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 30).Between("192.168.1.5:61000", "8.8.8.8:53")
                with { Mechanism = Mechanism.Udp }),
        ];
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
