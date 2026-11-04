using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// FX-RPC-002: RPC calls over TCP, eight clients at once asking the endpoint mapper on loopback where the service control
/// manager listens, as `icat record` kept them - the client's calls, the endpoint mapper's records of the calls it served,
/// and the TCP connections beneath them - read back as a session's records are (§5.1, ADR-004).
/// </summary>
public sealed class RpcOverTcpTests
{
    private static readonly Guid EndpointMapper = Guid.Parse("e1af8308-5d1f-11c9-91a4-08002b14a0fa");

    [Fact(DisplayName = "P4: FX-RPC-002's calls over TCP are counted once - 256 client calls paired by activity id, the endpoint mapper's 512 records pairing with nothing, and every byte the 8 connections' beneath them")]
    public void CallsOverTcpAreCountedOnce()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-RPC-002", "evidence");
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        int clientPid = scenario.RootElement.GetProperty("clientProcessId").GetInt32();
        int serverPid = scenario.RootElement.GetProperty("expectedServerProcessId").GetInt32();
        (List<ObservationRowV1> rows, List<SourceFieldRowV1> fields) = Ledgers(evidence, clientPid, serverPid);
        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == clientPid);
        ProcessNode server = overview.Nodes.Single(node => node.ProcessId == serverPid);

        // The client's 256 calls, each its start and the stop carrying its activity id, to the endpoint mapper's map
        // procedure, each start naming TCP.
        RpcChannelSummary made = Assert.Single(SessionRpcCalls.Channels(session.Store, client.Id).Channels);
        Assert.Equal((RpcCallSide.Client, (Guid?)EndpointMapper), (made.Side, made.Interface));
        Assert.Equal((256L, 256L, 0L, 0L, 512L),
            (made.Counts.Calls, made.Counts.Completed, made.Counts.Failed, made.Counts.NoActivityId, made.Records));
        RpcCall[] calls = [.. Enumerable.Range(0, 2).SelectMany(page =>
            SessionRpcCalls.Calls(session.Store, made.Key, page * 200, 200).Calls.Select(row => row.Call))];
        Assert.Equal(256, calls.Length);
        Assert.All(calls, call => Assert.Equal((3L, OperationText.TcpProtocol, 0L), (call.Procedure, call.Protocol, call.Status)));

        // The endpoint mapper's records of the calls it served over TCP carry no activity id, so each pairs with nothing:
        // 256 starts of the interface, each naming TCP, and 256 stops, which name none - never paired by time (P8).
        RpcChannelSummary[] served = [.. SessionRpcCalls.Channels(session.Store, server.Id).Channels
            .OrderBy(channel => channel.Interface?.ToString() ?? string.Empty, StringComparer.Ordinal)];
        Assert.Equal(
            [((Guid?)null, 256L, 0L, 256L, 256L), (EndpointMapper, 256L, 0L, 256L, 256L)],
            served.Select(channel => (channel.Interface, channel.Counts.Calls, channel.Counts.Completed,
                channel.Counts.NoActivityId, channel.Records)));
        Assert.All(served, channel => Assert.Equal(RpcCallSide.Server, channel.Side));

        // A row of either names the transport its start named, as the rung and its export list them.
        LadderRow madeRow = ChannelRungRows.Read(session.Store, made.Key, null, 1).Rows[0];
        Assert.Matches(@"^\d+(\.\d)? (ns|µs|ms|s) · procedure 3 · TCP · succeeded$", madeRow.Detail);
        LadderRow servedRow = ChannelRungRows.Read(session.Store, served[1].Key, null, 1).Rows[0];
        Assert.Equal("no activity id · procedure 3 · TCP · no status", servedRow.Detail);

        // Beneath them, one relationship: the 8 connections the client made to the endpoint mapper's 135. The capture held
        // no ALPC record, so no RPC relationship is drawn beside it, and the calls add none of its records.
        CommunicationEdge edge = Assert.Single(overview.Edges);
        Assert.Equal(Mechanism.Tcp, edge.Mechanism);
        Assert.Equal(8, edge.Evidence.Count);
        ushort[] mapperPort = [135];
        Assert.Equal(mapperPort, edge.AcceptedPorts);
        Assert.Equal(8, edge.SourceId == client.Id ? edge.SourceConnected : edge.TargetConnected);
        Assert.Equal(rows.LongCount(row => row.Mechanism == Mechanism.Tcp), edge.ObservationCount);

        // Every byte is a TCP record's, each end's the other's mirror: an RPC record states none, so nothing is counted twice.
        Assert.All(rows.Where(row => row.Mechanism == Mechanism.Rpc), row => Assert.Null(row.ByteValue));
        SessionByteMeasures bytes = SessionByteRanking.Measure(session.Store, null);
        TransportBytes sent = bytes.ByProcess[client.Id];
        TransportBytes answered = bytes.ByProcess[server.Id];
        Assert.Equal((44_288L, 264L, 44_728L, 264L), (sent.SentBytes, sent.SentMeasured, sent.ReceivedBytes, sent.ReceivedMeasured));
        Assert.Equal((sent.SentBytes, sent.ReceivedBytes), (answered.ReceivedBytes, answered.SentBytes));

        // The truth: 256 calls the client logged, each resolved, made on the 8 connections the system listed by local port,
        // which are the 8 the records hold.
        JsonElement[] truth = [.. File.ReadLines(Path.Combine(evidence, "truth-client.jsonl")).Select(line => JsonDocument.Parse(line).RootElement)];
        Assert.Equal(256, truth.Count(record => record.GetProperty("kind").GetString() == "CallCompleted"
            && record.GetProperty("status").GetString()!.StartsWith("resolved to ncacn_ip_tcp:127.0.0.1[", StringComparison.Ordinal)));
        int[] listed = [.. truth.Where(record => record.GetProperty("kind").GetString() == "ConnectionEstablished")
            .Select(record => record.GetProperty("localPort").GetInt32()).Order()];
        int[] connected = [.. rows.Where(row => row.Kind == ObservationKind.Connect).Select(row => (int)row.SourceEndpointPort!.Value).Order()];
        Assert.Equal(listed, connected);
    }

    /// <summary>
    /// The fixture's two ledgers as a session's rows: each call record with the procedure and protocol its start carried,
    /// on its own thread, and each TCP record with its bytes and ports, the two processes witnessed running.
    /// </summary>
    private static (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields) Ledgers(string evidence, int clientPid, int serverPid)
    {
        const long Origin = 10_000_000;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Inventory, clientPid, 1),
            Lifecycle(2, ObservationKind.Inventory, serverPid, 2),
        };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 10;
        foreach (string[] field in File.ReadLines(Path.Combine(evidence, "calls.csv")).Skip(1).Select(line => line.Split(',')))
        {
            bool start = field[2] == "start";
            int activity = int.Parse(field[5], CultureInfo.InvariantCulture);
            ObservationRowV1 row = RpcCall(
                Origin + long.Parse(field[0], CultureInfo.InvariantCulture),
                start ? ObservationKind.RequestStart : ObservationKind.RequestEnd,
                field[1] == "client" ? Direction.Outbound : Direction.Inbound,
                field[3] == "client" ? clientPid : serverPid,
                ordinal++,
                activity == 0 ? null : new Guid(activity, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2),
                field[6].Length == 0 ? null : Guid.Parse(field[6]),
                field[9].Length == 0 ? null : long.Parse(field[9], CultureInfo.InvariantCulture)) with
            {
                HeaderThreadId = int.Parse(field[4], CultureInfo.InvariantCulture),
            };
            rows.Add(row);
            if (field[7].Length > 0) fields.Add(Field(row, SourceField.RpcProcedureNumber, long.Parse(field[7], CultureInfo.InvariantCulture)));
            if (field[8].Length > 0) fields.Add(Field(row, SourceField.RpcProtocolSequence, long.Parse(field[8], CultureInfo.InvariantCulture)));
        }

        foreach (string[] field in File.ReadLines(Path.Combine(evidence, "transport.csv")).Skip(1).Select(line => line.Split(',')))
        {
            (ObservationKind kind, AccountingSide side) = field[2] switch
            {
                "send" => (ObservationKind.Send, AccountingSide.SendSide),
                "receive" => (ObservationKind.Receive, AccountingSide.ReceiveSide),
                "connect" => (ObservationKind.Connect, AccountingSide.EndpointActivity),
                "accept" => (ObservationKind.Accept, AccountingSide.EndpointActivity),
                "disconnect" => (ObservationKind.Disconnect, AccountingSide.EndpointActivity),
                _ => throw new InvalidDataException($"A transport record of kind {field[2]} is not one the fixture holds."),
            };
            int owner = field[1] == "client" ? clientPid : serverPid;
            rows.Add(Transfer(
                    Origin + long.Parse(field[0], CultureInfo.InvariantCulture),
                    kind,
                    side,
                    field[3].Length == 0 ? null : long.Parse(field[3], CultureInfo.InvariantCulture),
                    owner,
                    ordinal++)
                .Between("127.0.0.1:" + field[4], "127.0.0.1:" + field[5]) with
            {
                HeaderThreadId = int.Parse(field[6], CultureInfo.InvariantCulture),
            });
        }

        return ([.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)], fields);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root holding InterCat.slnx was not found.");
    }
}
