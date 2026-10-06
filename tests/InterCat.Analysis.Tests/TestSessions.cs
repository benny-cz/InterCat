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

    private static Guid RpcActivity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

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
        bool finished = false)
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
    public TemporarySession()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "InterCat.Analysis.Tests.Metrics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), TestSessions.Session, "metric-tests");
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
