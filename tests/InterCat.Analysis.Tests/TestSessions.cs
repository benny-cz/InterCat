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
    /// as every admitted TCP descriptor names them. Each endpoint is written "a.b.c.d:port".
    /// </summary>
    public static ObservationRowV1 Between(this ObservationRowV1 row, string local, string remote)
    {
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
        IReadOnlyList<SourceFieldRowV1>? fields = null)
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
            Committed,
            new() { RowsPerSegment = rowsPerSegment });
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
                sourceClock.Id));
        }

        foreach (SourceFieldRowV1 field in fields ?? [])
        {
            builder.AddFieldRow(field);
        }

        return builder.Complete(Committed);
    }

    private static RecordEnvelopeV1 Envelope(ObservationRowV1 row, uint schema, uint policy, CaptureId capture, ClockId clock) => new()
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
        Body = BodyV1.None,
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
