using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Builds small published sessions the way a real derivation does: one admitted journal record per derived row, so
/// the committed boundary each generation carries describes evidence that is actually in its journal.
/// </summary>
internal static class TestSessions
{
    public static readonly Guid Session = Guid.Parse("d6e7f8a9-b0c1-4d2e-8f3a-4b5c6d7e8f90");
    public static readonly CaptureId Capture = new(Guid.Parse("e7f8a9b0-c1d2-4e3f-8a4b-5c6d7e8f9a0b"));
    public static readonly ClockId Clock = new(Guid.Parse("33334444-5555-4666-8777-888899990000"));
    public static readonly Guid NetworkProvider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    public static readonly Guid ProcessProvider = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    public static readonly Guid RpcProvider = Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39");
    public static readonly Guid AlpcClass = Guid.Parse("45d8cccd-539f-4b72-a8b7-5c683142609a");
    public static readonly DateTimeOffset Committed = new(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);

    public static SourceClockDescriptor TestClock { get; } = ClockFor(Clock, "session-metrics-tests");

    public static SourceClockDescriptor ClockFor(ClockId clock, string host) => new(
        clock,
        HostId.Derive(host),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        0,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    /// <summary>A TCP transfer record whose payload names <paramref name="owner"/> and one byte slot.</summary>
    public static ObservationRowV1 Transfer(
        long ticks,
        ObservationKind kind,
        AccountingSide side,
        long? bytes,
        int? owner,
        ulong? ordinal = null) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal ?? (ulong)ticks,
        FactKey = FactKey.Create("network-transfer"),
        ProviderId = NetworkProvider,
        EventId = 10,
        DescriptorVersion = 0,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = 10,
        NativeTicks = ticks,
        HeaderProcessId = owner ?? 4,
        HeaderThreadId = (owner ?? 4) + 1,
        ProcessorNumber = 0,
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = kind,
        Direction = kind == ObservationKind.Receive ? Direction.Inbound : Direction.Outbound,
        OwnerProcessId = owner,
        ByteValue = bytes,
        ByteDomain = ByteDomain.TransportObserved,
        AccountingSide = side,
        MeasurementUnit = MeasurementUnit.Bytes,
        ByteAvailability = bytes is null ? FieldAvailability.NotExposed : FieldAvailability.Present,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = owner is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = bytes is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,
        TimingQuality = QualityLevel.Proven,
    };

    /// <summary>
    /// The same record with the endpoint pair its source names: the record's own endpoint first, then the remote one,
    /// as every admitted TCP descriptor names them. Each endpoint is written "a.b.c.d:port", or "[IPv6 address]:port"
    /// for a pair in the IPv6 columns.
    /// </summary>
    public static ObservationRowV1 Between(this ObservationRowV1 row, string local, string remote)
    {
        if (local.StartsWith('['))
        {
            (UInt128 localAddress6, ushort localPort6) = Endpoint6(local);
            (UInt128 remoteAddress6, ushort remotePort6) = Endpoint6(remote);
            return row with
            {
                EndpointAddressFamily = 6,
                SourceEndpointAddress = null,
                SourceEndpointAddressV6 = localAddress6,
                SourceEndpointPort = localPort6,
                DestinationEndpointAddress = null,
                DestinationEndpointAddressV6 = remoteAddress6,
                DestinationEndpointPort = remotePort6,
            };
        }

        (uint localAddress, ushort localPort) = Endpoint(local);
        (uint remoteAddress, ushort remotePort) = Endpoint(remote);
        return row with
        {
            EndpointAddressFamily = 4,
            SourceEndpointAddress = localAddress,
            SourceEndpointPort = localPort,
            DestinationEndpointAddress = remoteAddress,
            DestinationEndpointPort = remotePort,
        };

        static (UInt128 Address, ushort Port) Endpoint6(string text)
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            byte[] bytes = System.Net.IPAddress.Parse(text[1..close]).GetAddressBytes();
            return (
                System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes),
                ushort.Parse(text[(close + 2)..], System.Globalization.CultureInfo.InvariantCulture));
        }

        static (uint Address, ushort Port) Endpoint(string text)
        {
            string[] parts = text.Split(':');
            byte[] octets = [.. parts[0].Split('.').Select(part => byte.Parse(part, System.Globalization.CultureInfo.InvariantCulture))];
            return (
                ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3],
                ushort.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// A process lifecycle record: a creation, an exit or a capture-state rundown of <paramref name="processId"/>. It
    /// declares no byte field, and an exit carries the exit code the source reported.
    /// </summary>
    public static ObservationRowV1 Lifecycle(
        long ticks,
        ObservationKind kind,
        int processId,
        ulong ordinal,
        long? exitCode = null) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        FactKey = FactKey.Create("process-lifecycle"),
        ProviderId = ProcessProvider,
        EventId = kind switch
        {
            ObservationKind.Create => (ushort)1,
            ObservationKind.Exit => (ushort)2,
            _ => (ushort)15,
        },
        DescriptorVersion = kind == ObservationKind.Create ? (byte)4 : (byte)2,
        SchemaFingerprint = "sha256:" + new string('b', 64),
        Opcode = 0,
        NativeTicks = ticks,
        HeaderProcessId = 4,
        HeaderThreadId = 8,
        ProcessorNumber = 0,
        Mechanism = Mechanism.ProcessLifecycle,
        Layer = ObservationLayer.Lifecycle,
        Kind = kind,
        Direction = Direction.DirectionNotApplicable,
        OwnerProcessId = processId,
        ByteAvailability = FieldAvailability.NotApplicable,
        StatusCode = exitCode,
        StatusAvailability = exitCode is null ? FieldAvailability.NotApplicable : FieldAvailability.Present,
        AttributionQuality = QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.UnknownQuality,
        TimingQuality = QualityLevel.Proven,
    };

    /// <summary>
    /// An RPC call record raised in <paramref name="raisedBy"/>: a client call is raised in the calling process and a
    /// server call in the serving one. Its payload names no owner, so only its header says whose it is (ADR-030). A start
    /// carries the interface, and a stop its status.
    /// </summary>
    public static ObservationRowV1 RpcCall(
        long ticks,
        ObservationKind kind,
        Direction direction,
        int raisedBy,
        ulong ordinal,
        Guid? activity = null,
        Guid? interfaceUuid = null,
        long? status = null) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        FactKey = FactKey.Create("rpc-call"),
        ProviderId = RpcProvider,
        EventId = (ushort)((kind == ObservationKind.RequestStart ? 5 : 7) + (direction == Direction.Inbound ? 1 : 0)),
        DescriptorVersion = 1,
        SchemaFingerprint = "sha256:" + new string('c', 64),
        Opcode = kind == ObservationKind.RequestStart ? (byte)1 : (byte)2,
        NativeTicks = ticks,
        HeaderProcessId = raisedBy,
        HeaderThreadId = raisedBy + 1,
        ProcessorNumber = 0,
        Mechanism = Mechanism.Rpc,
        Layer = ObservationLayer.Application,
        Kind = kind,
        Direction = direction,
        ActivityId = activity,
        SourceIdentifier = kind == ObservationKind.RequestStart ? interfaceUuid : null,
        ByteAvailability = FieldAvailability.NotApplicable,
        StatusCode = kind == ObservationKind.RequestEnd ? status : null,
        StatusAvailability = kind == ObservationKind.RequestEnd && status is not null
            ? FieldAvailability.Present
            : FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.UnknownQuality,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.UnknownQuality,
        TimingQuality = QualityLevel.Proven,
    };

    /// <summary>
    /// An ALPC send or receive the thread <paramref name="thread"/> of <paramref name="process"/> made, as a classic kernel
    /// record names it: ALPC's class, id 0, version 2 and its opcode (ADR-035). Its message id is a source field.
    /// </summary>
    public static ObservationRowV1 Alpc(long ticks, ObservationKind kind, int process, int thread, ulong ordinal) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        FactKey = FactKey.Create("alpc-message"),
        ProviderId = AlpcClass,
        EventId = 0,
        DescriptorVersion = 2,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = kind == ObservationKind.Send ? (byte)33 : (byte)34,
        NativeTicks = ticks,
        HeaderProcessId = process,
        HeaderThreadId = thread,
        ProcessorNumber = 0,
        Mechanism = Mechanism.Alpc,
        Layer = ObservationLayer.Transport,
        Kind = kind,
        Direction = kind == ObservationKind.Send ? Direction.Outbound : Direction.Inbound,
        ByteAvailability = FieldAvailability.NotApplicable,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.UnknownQuality,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.UnknownQuality,
        TimingQuality = QualityLevel.Proven,
    };

    /// <summary>
    /// A live capture's one-epoch ledger that collected a TCP descriptor, a UDP one, or both, each delivering 3 records, and
    /// whose session lost <paramref name="lost"/> events: what decides a capture's coverage of a connection.
    /// </summary>
    public static CoverageLedgerV1 TransportLedger(bool tcp, bool udp, long lost = 0) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.LiveCapture,
                FirstDeliveredNativeTicks = 0,
                LastDeliveredNativeTicks = 1_000_000,
                Collected =
                [
                    .. new[] { (tcp, 10, Mechanism.Tcp), (udp, 11, Mechanism.Udp) }.Where(entry => entry.Item1).Select(entry => new CoverageCollectedV1
                    {
                        ProviderId = NetworkProvider, ProviderName = "network", EventId = entry.Item2, Version = 0, Mechanism = entry.Item3,
                    }),
                ],
                Deliveries =
                [
                    .. new[] { (tcp, 10), (udp, 11) }.Where(entry => entry.Item1).Select(entry => new CoverageDeliveryV1
                    {
                        ProviderId = NetworkProvider, EventId = entry.Item2, Version = 0, Delivered = 3, Admitted = 3, Omitted = 0,
                    }),
                ],
                Losses =
                [
                    new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost },
                    new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
                ],
            },
        ],
    };

    /// <summary>
    /// An import's one-epoch ledger that collected RPC's call start, and ALPC's send when <paramref name="alpc"/> says so:
    /// what decides whether a generation's RPC calls are followed to their other ends (ADR-034).
    /// </summary>
    public static CoverageLedgerV1 RpcLedger(bool alpc) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.EtlImport,
                Collected =
                [
                    new CoverageCollectedV1
                    {
                        ProviderId = RpcProvider, ProviderName = "Microsoft-Windows-RPC", EventId = 5, Version = 1, Mechanism = Mechanism.Rpc,
                    },
                    .. alpc
                        ? new[]
                        {
                            new CoverageCollectedV1
                            {
                                ProviderId = AlpcClass, ProviderName = "Kernel ALPC", EventId = 0, Version = 2, Opcode = 33,
                                Mechanism = Mechanism.Alpc,
                            },
                        }
                        : [],
                ],
                Deliveries = [],
                Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }],
            },
        ],
    };

    /// <summary>The service control manager's RPC interface.</summary>
    public static readonly Guid ServiceControlInterface = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    /// <summary>
    /// A caller's three calls to the service control manager and the three the host served, the first and third linked by
    /// an ALPC message sent on the caller's thread and received on the thread that began the served call.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedRpcCalls()
    {
        ObservationRowV1[] messages =
        [
            Alpc(102, ObservationKind.Send, 400, 401, 60),
            Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61),
            Alpc(302, ObservationKind.Send, 400, 401, 62),
            Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63),
        ];
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Create, 400, 1),
            Lifecycle(2, ObservationKind.Inventory, 1_960, 2),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, RpcActivity(1), ServiceControlInterface),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, RpcActivity(1), status: 0),
            RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, RpcActivity(2), ServiceControlInterface),
            RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, RpcActivity(2), status: 0),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, RpcActivity(3), ServiceControlInterface),
            RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, RpcActivity(3), status: 5),
            RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, RpcActivity(11), ServiceControlInterface),
            RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, RpcActivity(11), status: 0),
            RpcCall(205, ObservationKind.RequestStart, Direction.Inbound, 1_960, 52, RpcActivity(12), ServiceControlInterface),
            RpcCall(207, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 53, RpcActivity(12), status: 0),
            RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, RpcActivity(13), ServiceControlInterface),
            RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, RpcActivity(13), status: 5),
            .. messages,
        ];
        return (
            rows,
            [
                .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                    .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
                .. messages.Select((message, index) => Field(message, SourceField.AlpcMessageId, 21 + (index / 2))),
            ]);
    }

    /// <summary>The processes of <see cref="PatternRows"/>: two browsers, a server, a resolver and an agent.</summary>
    public const int PatternBrowser = 3_000, PatternOtherBrowser = 3_001, PatternServer = 4_000, PatternResolver = 5_000,
        PatternAgent = 6_000;

    /// <summary>
    /// Two browsers' connections to the server's 8080, three; their connections to two hosts' 443; a host's connection to
    /// the server's 3389; the resolver's two UDP flows to 8.8.8.8's 53 and two to the server's 5353, and a host's flow to the
    /// server's 161; the agent's sends on connections it was not seen open, to another host and to the server, its flows
    /// between two dynamic ports, to another host and to the resolver, and its connection to a port on this machine no
    /// record holds; and a caller's three calls to the service control manager,
    /// which the host served.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) PatternRows()
    {
        (ObservationRowV1[] calls, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        ObservationRowV1[] rows =
        [
            Lifecycle(500, ObservationKind.Create, PatternBrowser, 501) with { ResourceName = @"C:\Apps\browser.exe" },
            Lifecycle(510, ObservationKind.Create, PatternOtherBrowser, 511) with { ResourceName = @"C:\Apps\browser.exe" },
            Lifecycle(520, ObservationKind.Create, PatternServer, 521) with { ResourceName = @"C:\Apps\server.exe" },
            Lifecycle(530, ObservationKind.Create, PatternResolver, 531) with { ResourceName = @"C:\Apps\resolver.exe" },
            Lifecycle(540, ObservationKind.Create, PatternAgent, 541) with { ResourceName = @"C:\Apps\agent.exe" },
            .. OpenedConnection(1_000, PatternBrowser, PatternServer, "127.0.0.1:50000", "127.0.0.1:8080"),
            .. OpenedConnection(1_100, PatternOtherBrowser, PatternServer, "127.0.0.1:50001", "127.0.0.1:8080"),
            .. OpenedConnection(8_000, PatternBrowser, PatternServer, "127.0.0.1:50002", "127.0.0.1:8080"),
            Transfer(2_000, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, PatternBrowser)
                .Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(2_001, ObservationKind.Send, AccountingSide.SendSide, 100, PatternBrowser)
                .Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(6_000, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, PatternOtherBrowser)
                .Between("192.168.1.10:52001", "142.250.1.2:443"),
            Transfer(6_001, ObservationKind.Send, AccountingSide.SendSide, 100, PatternOtherBrowser)
                .Between("192.168.1.10:52001", "142.250.1.2:443"),
            Transfer(3_000, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, PatternServer)
                .Between("192.168.1.10:3389", "10.0.0.5:51000"),
            Transfer(3_001, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, PatternServer)
                .Between("192.168.1.10:3389", "10.0.0.5:51000"),
            Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 40, PatternResolver)
                .Between("192.168.1.10:53000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp },
            Transfer(9_000, ObservationKind.Send, AccountingSide.SendSide, 40, PatternResolver)
                .Between("192.168.1.10:53001", "8.8.8.8:53") with { Mechanism = Mechanism.Udp },
            Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 10, PatternAgent)
                .Between("192.168.1.10:52002", "13.107.4.50:443"),
            Transfer(7_000, ObservationKind.Send, AccountingSide.SendSide, 10, PatternAgent)
                .Between("192.168.1.10:52003", "20.0.0.1:50001") with { Mechanism = Mechanism.Udp },
            Transfer(4_500, ObservationKind.Send, AccountingSide.SendSide, 30, PatternResolver)
                .Between("127.0.0.1:50100", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(4_501, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, PatternServer)
                .Between("127.0.0.1:50100", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(4_600, ObservationKind.Send, AccountingSide.SendSide, 30, PatternResolver)
                .Between("127.0.0.1:50101", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(4_601, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, PatternServer)
                .Between("127.0.0.1:50101", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(7_500, ObservationKind.Receive, AccountingSide.ReceiveSide, 20, PatternServer)
                .Between("10.0.0.9:55000", "192.168.1.10:161") with { Mechanism = Mechanism.Udp },
            Transfer(8_500, ObservationKind.Send, AccountingSide.SendSide, 30, PatternAgent)
                .Between("127.0.0.1:50200", "127.0.0.1:50300") with { Mechanism = Mechanism.Udp },
            Transfer(8_501, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, PatternResolver)
                .Between("127.0.0.1:50200", "127.0.0.1:50300") with { Mechanism = Mechanism.Udp },
            Transfer(9_700, ObservationKind.Send, AccountingSide.SendSide, 20, PatternAgent).Between("127.0.0.1:50400", "127.0.0.1:7000"),
            Transfer(9_701, ObservationKind.Receive, AccountingSide.ReceiveSide, 20, PatternServer)
                .Between("127.0.0.1:7000", "127.0.0.1:50400"),
            Transfer(9_500, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, PatternAgent)
                .Between("127.0.0.1:52010", "127.0.0.1:9000"),
            Transfer(9_501, ObservationKind.Send, AccountingSide.SendSide, 10, PatternAgent).Between("127.0.0.1:52010", "127.0.0.1:9000"),
            Lifecycle(10_000, ObservationKind.Exit, PatternAgent, 10_001),
            .. calls,
        ];
        return ([.. rows.OrderBy(row => row.NativeTicks).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })],
            fields);
    }

    /// <summary>
    /// One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened at both ends, and a send across it.
    /// </summary>
    private static ObservationRowV1[] OpenedConnection(long ticks, int maker, int taker, string from, string to) =>
    [
        Transfer(ticks, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker).Between(from, to),
        Transfer(ticks + 1, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, taker).Between(to, from),
        Transfer(ticks + 2, ObservationKind.Send, AccountingSide.SendSide, 64, maker).Between(from, to),
        Transfer(ticks + 3, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, taker).Between(to, from),
    ];

    private static Guid RpcActivity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    /// <summary>The PIDs of <see cref="WaitRows"/>'s reused process, its second server and its process of two short calls.</summary>
    public const int WaitReused = 500, WaitOtherServer = 1_970, WaitBrief = 600;

    /// <summary>A second interface PID 400 calls PID 1960 at, and a third it calls PID 1970 at.</summary>
    public static readonly Guid WaitInterface = Guid.Parse("4b324fc8-1670-01d3-1278-5a47bf6ee188"),
        WaitOtherInterface = Guid.Parse("9b3195fe-d603-43d1-a0d5-9072d7cde122");

    /// <summary>
    /// The calls of <see cref="LinkedRpcCalls"/> and, beside them: a call PID 1960 serves at another interface and one PID
    /// 1970 serves, each linked through ALPC; client calls of PID 400 whose spans are not validated - one still open when the
    /// capture ended, one whose start was not recorded and one with no activity id; a completed call by a reused PID's
    /// later holder, which binds to it as a candidate; and two short calls of PID 600, together as long as PID 400's call no
    /// ALPC send carried.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) WaitRows()
    {
        (ObservationRowV1[] calls, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        ObservationRowV1 open = RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 80, RpcActivity(31), ServiceControlInterface);
        ObservationRowV1 anonymous = RpcCall(420, ObservationKind.RequestStart, Direction.Outbound, 400, 82, null, ServiceControlInterface);
        ObservationRowV1 reused = RpcCall(430, ObservationKind.RequestStart, Direction.Outbound, WaitReused, 86, RpcActivity(33),
            ServiceControlInterface);
        ObservationRowV1 firstBrief = RpcCall(450, ObservationKind.RequestStart, Direction.Outbound, WaitBrief, 89, RpcActivity(34),
            ServiceControlInterface);
        ObservationRowV1 secondBrief = RpcCall(460, ObservationKind.RequestStart, Direction.Outbound, WaitBrief, 91, RpcActivity(35),
            ServiceControlInterface);
        ObservationRowV1[] linked =
        [
            RpcCall(600, ObservationKind.RequestStart, Direction.Outbound, 400, 100, RpcActivity(41), WaitInterface),
            Alpc(602, ObservationKind.Send, 400, 401, 101),
            Alpc(603, ObservationKind.Receive, 1_960, 1_961, 102),
            RpcCall(605, ObservationKind.RequestStart, Direction.Inbound, 1_960, 103, RpcActivity(51), WaitInterface),
            RpcCall(608, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 104, RpcActivity(51), status: 0),
            RpcCall(615, ObservationKind.RequestEnd, Direction.Outbound, 400, 105, RpcActivity(41), status: 0),
            RpcCall(700, ObservationKind.RequestStart, Direction.Outbound, 400, 110, RpcActivity(42), WaitOtherInterface),
            Alpc(702, ObservationKind.Send, 400, 401, 111),
            Alpc(703, ObservationKind.Receive, WaitOtherServer, WaitOtherServer + 1, 112),
            RpcCall(704, ObservationKind.RequestStart, Direction.Inbound, WaitOtherServer, 113, RpcActivity(52), WaitOtherInterface),
            RpcCall(706, ObservationKind.RequestEnd, Direction.Inbound, WaitOtherServer, 114, RpcActivity(52), status: 0),
            RpcCall(710, ObservationKind.RequestEnd, Direction.Outbound, 400, 115, RpcActivity(42), status: 0),
        ];
        ObservationRowV1[] rows =
        [
            .. calls,
            open,
            RpcCall(410, ObservationKind.RequestEnd, Direction.Outbound, 400, 81, RpcActivity(32), status: 0),
            anonymous,
            Lifecycle(3, ObservationKind.Create, WaitReused, 83),
            Lifecycle(4, ObservationKind.Exit, WaitReused, 84),
            Lifecycle(5, ObservationKind.Create, WaitReused, 85),
            reused,
            RpcCall(440, ObservationKind.RequestEnd, Direction.Outbound, WaitReused, 87, RpcActivity(33), status: 0),
            Lifecycle(6, ObservationKind.Inventory, WaitBrief, 88),
            firstBrief,
            RpcCall(455, ObservationKind.RequestEnd, Direction.Outbound, WaitBrief, 90, RpcActivity(34), status: 0),
            secondBrief,
            RpcCall(465, ObservationKind.RequestEnd, Direction.Outbound, WaitBrief, 92, RpcActivity(35), status: 0),
            Lifecycle(7, ObservationKind.Inventory, WaitOtherServer, 93),
            .. linked,
        ];
        return (
            [.. rows.OrderBy(row => row.NativeTicks).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })],
            [
                .. fields,
                Field(open, SourceField.RpcProcedureNumber, 7),
                Field(anonymous, SourceField.RpcProcedureNumber, 7),
                Field(reused, SourceField.RpcProcedureNumber, 7),
                Field(firstBrief, SourceField.RpcProcedureNumber, 7),
                Field(secondBrief, SourceField.RpcProcedureNumber, 7),
                .. linked.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                    .Select(start => Field(start, SourceField.RpcProcedureNumber, 2)),
                .. linked.Where(row => row.Mechanism == Mechanism.Alpc)
                    .Select((message, index) => Field(message, SourceField.AlpcMessageId, 23 + (index / 2))),
            ]);
    }

    /// <summary>The processes of <see cref="LinkRows"/>, by PID.</summary>
    public const int LinkShell = 300, LinkApp = 400, LinkServices = 1_960, LinkServer = 4_000, LinkOther = 410, LinkSibling = 430,
        LinkReused = 77, LinkReusedChild = 420, LinkLate = 450;

    /// <summary>
    /// A known dependency and unrelated activity beside it (`contracts/links-v1.md`): shell.exe starts app.exe, which names it
    /// by its start key; two of app.exe's three calls to svcctl are served by services.exe through ALPC, the third carried by
    /// no ALPC send, as <see cref="LinkedRpcCalls"/> holds them; app.exe connects to
    /// server.exe at 8080, the capture seeing both ends open, to another host at 443 and to an end on this machine no record
    /// holds at 9100, and another host connects to server.exe at 3389. Beside them, other.exe is created two readings after
    /// app.exe by a parent the capture does not hold, and makes a call at the same moment as app.exe's first, which no ALPC
    /// send carries; shell.exe starts sibling.exe, which names it by its PID alone; PID 77 is held twice, and child.exe names
    /// it by PID while its second holder runs; late.exe is created two seconds after the rest. app.exe also sends a UDP
    /// datagram to 8.8.8.8:53 and a flow of them to server.exe at 5353, exchanges data with server.exe at 9000 and with
    /// another host at 80 with no open seen; server.exe connects to itself at 7000; and services.exe serves one more call
    /// than its linked ones, which no client call reached.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkRows()
    {
        (ObservationRowV1[] calls, SourceFieldRowV1[] callFields) = LinkedRpcCalls();
        ObservationRowV1 app = calls[0] with { ResourceName = @"C:\Apps\app.exe" };
        ObservationRowV1 services = calls[1] with { ResourceName = @"C:\Windows\System32\services.exe" };
        ObservationRowV1 shell = Lifecycle(0, ObservationKind.Inventory, LinkShell, 5_000) with { ResourceName = @"C:\Windows\shell.exe" };
        ObservationRowV1 other = Lifecycle(3, ObservationKind.Create, LinkOther, 5_001) with { ResourceName = @"C:\Apps\other.exe" };
        ObservationRowV1 sibling = Lifecycle(4, ObservationKind.Create, LinkSibling, 5_002) with { ResourceName = @"C:\Apps\sibling.exe" };
        ObservationRowV1 reused = Lifecycle(5, ObservationKind.Create, LinkReused, 5_003) with { ResourceName = @"C:\Apps\old.exe" };
        ObservationRowV1 exited = Lifecycle(6, ObservationKind.Exit, LinkReused, 5_004);
        ObservationRowV1 reusing = Lifecycle(7, ObservationKind.Create, LinkReused, 5_005) with { ResourceName = @"C:\Apps\new.exe" };
        ObservationRowV1 child = Lifecycle(8, ObservationKind.Create, LinkReusedChild, 5_006) with { ResourceName = @"C:\Apps\child.exe" };
        ObservationRowV1 server = Lifecycle(9, ObservationKind.Inventory, LinkServer, 5_007) with { ResourceName = @"C:\Apps\server.exe" };
        ObservationRowV1 late = Lifecycle(20_000_000, ObservationKind.Create, LinkLate, 5_008) with { ResourceName = @"C:\Apps\late.exe" };
        ObservationRowV1 unlinkedStart = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, LinkOther, 70, RpcActivity(21),
            ServiceControlInterface);
        ObservationRowV1 unreachedStart = RpcCall(500, ObservationKind.RequestStart, Direction.Inbound, LinkServices, 72, RpcActivity(14),
            ServiceControlInterface);
        ObservationRowV1[] rows =
        [
            shell, app, services, other, sibling, reused, exited, reusing, child, server, late,
            .. calls[2..],
            unlinkedStart,
            RpcCall(118, ObservationKind.RequestEnd, Direction.Outbound, LinkOther, 71, RpcActivity(21), status: 0),
            unreachedStart,
            RpcCall(510, ObservationKind.RequestEnd, Direction.Inbound, LinkServices, 73, RpcActivity(14), status: 0),
            .. OpenedConnection(1_000, LinkApp, LinkServer, "127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(1_100, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, LinkApp)
                .Between("192.168.1.10:52000", "93.184.216.34:443"),
            Transfer(1_101, ObservationKind.Send, AccountingSide.SendSide, 100, LinkApp).Between("192.168.1.10:52000", "93.184.216.34:443"),
            Transfer(1_200, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, LinkServer)
                .Between("192.168.1.10:3389", "10.0.0.5:51000"),
            Transfer(1_201, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, LinkServer).Between("192.168.1.10:3389", "10.0.0.5:51000"),
            Transfer(1_300, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, LinkApp).Between("127.0.0.1:52010", "127.0.0.1:9100"),
            Transfer(1_301, ObservationKind.Send, AccountingSide.SendSide, 10, LinkApp).Between("127.0.0.1:52010", "127.0.0.1:9100"),
            Transfer(1_400, ObservationKind.Send, AccountingSide.SendSide, 20, LinkApp).Between("127.0.0.1:50400", "127.0.0.1:9000"),
            Transfer(1_401, ObservationKind.Receive, AccountingSide.ReceiveSide, 20, LinkServer).Between("127.0.0.1:9000", "127.0.0.1:50400"),
            Transfer(1_500, ObservationKind.Send, AccountingSide.SendSide, 40, LinkApp)
                .Between("192.168.1.10:53000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp },
            Transfer(1_600, ObservationKind.Send, AccountingSide.SendSide, 30, LinkApp)
                .Between("127.0.0.1:50500", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(1_601, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, LinkServer)
                .Between("127.0.0.1:50500", "127.0.0.1:5353") with { Mechanism = Mechanism.Udp },
            Transfer(1_700, ObservationKind.Send, AccountingSide.SendSide, 10, LinkApp).Between("192.168.1.10:52100", "151.101.1.1:80"),
            .. OpenedConnection(1_800, LinkServer, LinkServer, "127.0.0.1:50600", "127.0.0.1:7000"),
        ];
        return (
            [.. rows.OrderBy(row => row.NativeTicks).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })],
            [
                .. callFields,
                Field(shell, SourceField.ProcessStartSequence, 900),
                Field(app, SourceField.ProcessStartSequence, 901),
                Field(app, SourceField.ParentProcessId, LinkShell),
                Field(app, SourceField.ParentStartSequence, 900),
                Field(other, SourceField.ParentProcessId, 12),
                Field(sibling, SourceField.ParentProcessId, LinkShell),
                Field(child, SourceField.ParentProcessId, LinkReused),
                Field(unlinkedStart, SourceField.RpcProcedureNumber, 7),
                Field(unreachedStart, SourceField.RpcProcedureNumber, 7),
            ]);
    }

    /// <summary>
    /// Five exchanges of process 4242: exchange 1 in six buffers, its request and response bodies in two each, the request
    /// body's last raised after the response ended, interleaved with exchange 2's three, whose request had no body; exchange
    /// 3, whose response body's last buffer was not recorded; exchange 1's number again; and exchange 3's again, its request
    /// head lost. Process 7 made none. The buffers come from raw stream <paramref name="stream"/>, those of the last two
    /// exchanges from <paramref name="laterStream"/> when it is given.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) HttpExchangeRows(uint stream = 1, uint? laterStream = null)
    {
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes)[] buffers =
        [
            (10, 2001, 1, 0, 3, 181), (11, 2002, 1, 0, 1, 64),
            (15, 2001, 2, 0, 3, 181),
            (20, 2003, 1, 0, 3, 115),
            (25, 2003, 2, 0, 3, 115), (26, 2004, 2, 0, 3, 0),
            (29, 2004, 1, 0, 1, 900), (30, 2004, 1, 1, 2, 100), (31, 2002, 1, 1, 2, 36),
            (40, 2001, 3, 0, 3, 181), (41, 2003, 3, 0, 3, 115), (42, 2004, 3, 0, 1, 50),
            (100, 2001, 1, 0, 3, 181), (101, 2003, 1, 0, 3, 115), (102, 2004, 1, 0, 3, 7),
            (200, 2003, 3, 0, 3, 115), (201, 2004, 3, 0, 3, 9),
        ];
        ObservationRowV1[] rows =
        [
            .. buffers.Select((buffer, index) => Http(buffer.Ticks, buffer.Event, (ulong)(index + 1), buffer.Bytes) with
            {
                RawStreamId = index >= 12 && laterStream is { } later ? later : stream,
            }),
        ];
        SourceFieldRowV1[] fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, buffer.Number),
                Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        return ([Lifecycle(1, ObservationKind.Create, 4_242, 100), Lifecycle(2, ObservationKind.Inventory, 7, 101), .. rows], fields);
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242, with a session time.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
            SessionRelativeTicks = ticks * 100,
        };

    /// <summary>
    /// A machine's processes in their terminal sessions (§6.3): client.exe in session 1 beside server.exe, to which it
    /// sends, and again in session 2, sending to service.exe in session 0, to which server.exe sends too; tool.exe, whose
    /// lifecycle records name no session, sends to an address nothing in the capture holds. The rows' session times are a
    /// hundred times their readings.
    /// </summary>
    public static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) TerminalSessions()
    {
        ObservationRowV1[] processes =
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 200 },
            Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 300 },
            Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Windows\service.exe", SessionRelativeTicks = 400 },
            Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\tool.exe", SessionRelativeTicks = 500 },
        ];
        return (
        [
            .. processes,
            .. Exchange(100, "127.0.0.1:50000", 200, "127.0.0.1:8080", 10, 64, 10),
            .. Exchange(101, "127.0.0.1:50001", 300, "127.0.0.1:9090", 20, 32, 20),
            .. Exchange(200, "127.0.0.1:50002", 300, "127.0.0.1:9090", 30, 16, 30),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 400, 40).Between("127.0.0.1:50003", "10.0.0.5:443")
                with { SessionRelativeTicks = 4_000 },
        ],
        [
            Field(processes[0], SourceField.ProcessSessionId, 1),
            Field(processes[1], SourceField.ProcessSessionId, 2),
            Field(processes[2], SourceField.ProcessSessionId, 1),
            Field(processes[3], SourceField.ProcessSessionId, 0),
        ]);

        static ObservationRowV1[] Exchange(int client, string near, int server, string far, long ticks, long bytes, ulong ordinal) =>
        [
            Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, bytes, client, ordinal).Between(near, far)
                with { SessionRelativeTicks = ticks * 100 },
            Transfer(ticks + 1, ObservationKind.Receive, AccountingSide.ReceiveSide, bytes, server, ordinal + 1).Between(far, near)
                with { SessionRelativeTicks = (ticks + 1) * 100 },
        ];
    }

    /// <summary>When the broker <see cref="PublishCollected"/> names was created.</summary>
    public static readonly DateTimeOffset CollectorBrokerCreated = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(3);

    /// <summary>
    /// The broker, PID 4120, created at <see cref="CollectorBrokerCreated"/>, sends to the client, PID 7008, whose lifecycle
    /// record carries no creation time, over one loopback connection; the broker then exits, and tool.exe takes its PID a
    /// second later. Named, the collectors are the broker, the client, and a recorder of the broker's PID created when no
    /// process holding it was.
    /// </summary>
    public static void PublishCollected(SessionStore store, bool named = true)
    {
        ObservationRowV1 broker = Lifecycle(1, ObservationKind.Create, 4120, 1) with
        {
            ResourceName = @"C:\Program Files\InterCat\intercat-broker.exe", SessionRelativeTicks = 100,
        };
        ObservationRowV1 client = Lifecycle(2, ObservationKind.Create, 7008, 2) with
        {
            ResourceName = @"C:\Program Files\InterCat\InterCat.exe", SessionRelativeTicks = 200,
        };
        ObservationRowV1 send = Transfer(3, ObservationKind.Send, AccountingSide.SendSide, 64, 4120, 3)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 300 };
        ObservationRowV1 receive = Transfer(4, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 7008, 4)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 400 };
        ObservationRowV1 exit = Lifecycle(5, ObservationKind.Exit, 4120, 5) with { SessionRelativeTicks = 500 };
        ObservationRowV1 later = Lifecycle(6, ObservationKind.Create, 4120, 6) with
        {
            ResourceName = @"C:\Tools\tool.exe", SessionRelativeTicks = 600,
        };
        Publish(store, [broker, client, send, receive, exit, later],
            fields:
            [
                Field(broker, SourceField.ProcessCreateTime, CollectorBrokerCreated.ToFileTime()),
                Field(later, SourceField.ProcessCreateTime, CollectorBrokerCreated.AddSeconds(1).ToFileTime()),
            ],
            collectors: !named ? null : new CollectorIdentitiesV1
            {
                Contract = CollectorIdentitiesV1.ContractName,
                CaptureId = Capture.Value,
                Processes =
                [
                    new() { Role = CollectorRole.Broker, ProcessId = 4120, CreatedUtc = CollectorBrokerCreated },
                    new() { Role = CollectorRole.Client, ProcessId = 7008 },
                    new() { Role = CollectorRole.Recorder, ProcessId = 4120, CreatedUtc = CollectorBrokerCreated.AddSeconds(5) },
                ],
            });
    }

    /// <summary>A source field of an observation, as a provider supplied it beside the row.</summary>
    public static SourceFieldRowV1 Field(ObservationRowV1 observation, SourceField code, long value) => new()
    {
        RawStreamId = observation.RawStreamId,
        RawSourceEpoch = observation.RawSourceEpoch,
        RawRecordOrdinal = observation.RawRecordOrdinal,
        FactKey = observation.FactKey,
        NativeTicks = observation.NativeTicks,
        Field = code,
        Value = value,
        Availability = FieldAvailability.Present,
    };

    /// <summary>Publishes one generation holding these rows, with the admitted journal records they derive from.</summary>
    public static DerivedGenerationResult Publish(
        SessionStore store,
        IReadOnlyList<ObservationRowV1> rows,
        int rowsPerSegment = 250_000,
        NormalizerContractVersion? derivation = null,
        CaptureId? capture = null,
        SourceClockDescriptor? clock = null,
        IReadOnlyList<SourceFieldRowV1>? fields = null,
        CoverageLedgerV1? coverage = null,
        Func<ObservationRowV1, BodyV1>? bodyForRow = null,
        int journalBatchRecords = 4_096,
        DateTimeOffset? committedUtc = null,
        (ContentChunkHeaderV1 Header, IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Fragments)? content = null,
        ClockCalibrationV1? calibration = null,
        bool finished = false,
        CollectorIdentitiesV1? collectors = null,
        byte[]? plan = null,
        ILiveIndexer? indexer = null)
    {
        SourceClockDescriptor sourceClock = clock ?? TestClock;
        CaptureId captureId = capture ?? Capture;
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(
            store,
            new SegmentIdentityV1
            {
                CaptureId = captureId,
                ClockId = sourceClock.Id,
                TimestampEncoding = TimestampEncoding.Qpc,
                Derivation = derivation ?? NormalizerContractVersion.V1,
            },
            sourceClock,
            committedUtc ?? Committed,
            new() { RowsPerSegment = rowsPerSegment, JournalBatchRecords = journalBatchRecords });
        var schemas = new JournalV1SchemaTable();
        var references = new Dictionary<(Guid, ushort, byte, string), uint>();
        foreach (ObservationRowV1 row in rows)
        {
            (Guid, ushort, byte, string) key = (row.ProviderId, row.EventId, row.DescriptorVersion, row.SchemaFingerprint);
            if (!references.ContainsKey(key))
            {
                references[key] = schemas.Intern(row.ProviderId, row.EventId, row.DescriptorVersion, row.SchemaFingerprint);
            }
        }

        uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
        builder.Journal.WriteSchemas(schemas);
        foreach (ObservationRowV1 row in rows)
        {
            builder.AddRow(row);
            builder.Journal.Append(Envelope(
                row,
                references[(row.ProviderId, row.EventId, row.DescriptorVersion, row.SchemaFingerprint)],
                policy,
                captureId,
                sourceClock.Id,
                bodyForRow?.Invoke(row)));
        }

        foreach (SourceFieldRowV1 field in fields ?? [])
        {
            builder.AddFieldRow(field);
        }

        if (coverage is not null)
        {
            builder.StageCoverageLedger(coverage);
        }

        if (content is { } kept)
        {
            builder.StageContent(kept.Header, kept.Fragments);
        }

        if (calibration is not null)
        {
            builder.StageClockCalibration(calibration);
        }

        if (collectors is not null)
        {
            builder.StageCollectorIdentities(collectors);
        }

        // The normalizer plan the capture admitted its records under, as a recorder retains it beside its journal.
        if (plan is not null)
        {
            builder.StageNormalizerPlan(plan);
        }

        // A live follow indexes the generation this one extends into it, as its follower does.
        if (indexer is not null)
        {
            builder.StageIndexesOf(indexer);
        }

        // A capture that reached its last publication says so, as a recorder's final generation does.
        if (finished)
        {
            builder.StageCaptureFinalization(new CaptureFinalizationV1
            {
                Contract = CaptureFinalizationV1.ContractName,
                CaptureId = captureId.Value,
                FinalizedUtc = committedUtc ?? Committed,
                ProvidersStopped = true,
                CallbacksDrained = true,
            });
        }

        return builder.Complete(committedUtc ?? Committed);
    }

    /// <summary>A content chunk's header for <see cref="Capture"/>, under a test policy.</summary>
    public static ContentChunkHeaderV1 ContentHeader(
        int recordLimit = 64,
        ContentInspectionV1 inspection = ContentInspectionV1.HexAndText,
        CaptureId? capture = null) =>
        new(capture ?? Capture, "test-scoped-content-v1", recordLimit, inspection);

    /// <summary>
    /// The content of <paramref name="row"/>'s record: its <paramref name="message"/> as an outbound or inbound application
    /// payload, kept whole or truncated to <paramref name="recordLimit"/>.
    /// </summary>
    public static (ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes) Content(
        ObservationRowV1 row,
        ReadOnlyMemory<byte> message,
        int recordLimit = 64,
        ContentEncodingV1 encoding = ContentEncodingV1.Utf8)
    {
        bool truncated = message.Length > recordLimit;
        ReadOnlyMemory<byte> kept = truncated ? message[..recordLimit] : message;
        return (new ContentFragmentV1(
            row.RawStreamId,
            row.RawSourceEpoch,
            row.RawRecordOrdinal,
            ContentClassificationV1.ApplicationPayload,
            row.Direction is Direction.Outbound or Direction.Inbound ? row.Direction : Direction.UnknownDirection,
            encoding,
            truncated ? ContentDispositionV1.TruncatedByRecordLimit : ContentDispositionV1.Whole,
            0,
            message.Length,
            kept.Length), kept);
    }

    private static RecordEnvelopeV1 Envelope(ObservationRowV1 row, uint schema, uint policy, CaptureId capture,
        ClockId clock, BodyV1? body = null) => new()
    {
        CaptureId = capture,
        StreamId = row.RawStreamId,
        SourceEpoch = row.RawSourceEpoch,
        RecordOrdinal = row.RawRecordOrdinal,
        Header = new(
            row.ProviderId,
            row.EventId,
            row.DescriptorVersion,
            0,
            0,
            row.Opcode,
            0,
            0,
            0,
            0,
            row.HeaderProcessId,
            row.HeaderThreadId,
            Guid.Empty,
            Guid.Empty),
        BufferContext = new(row.ProcessorNumber, 0),
        ClockId = clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = row.NativeTicks,
        PointerSize = 8,
        SchemaReference = schema,
        AdmissionPolicyReference = policy,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = body ?? BodyV1.None,
    };

    /// <summary>Every segment the store's current generation names, opened.</summary>
    public static IReadOnlyList<SegmentReaderV1> Segments(SessionStore store) =>
        [.. SessionSegments.Names(store.Current!).Select(name => SessionSegments.Open(store.Root, store.Current!, name))];
}

/// <summary>A session directory that exists for one test and is removed after it.</summary>
internal sealed class TemporarySession : IDisposable
{
    /// <summary>A session in a folder of its own: the tests' one session, or <paramref name="session"/> where another capture is.</summary>
    public TemporarySession(Guid? session = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "InterCat.Analysis.Tests.Metrics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), session ?? TestSessions.Session, "metric-tests");
    }

    public string Path { get; }

    public SessionStore Store { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
