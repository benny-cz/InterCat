using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What making the demo investigation wrote: its folder, its investigation file and its two sessions.</summary>
public sealed record DemoInvestigationResult(string Directory, string WorkspacePath, string ClientSession, string ServerSession);

/// <summary>
/// The demo investigation M5's exit gate ships: two hosts that exchange TCP, one of which also sends UDP to a third it
/// never captured, each with a session of its own, aligned by their recorded wall clocks, with every mechanism neither
/// capture collected listed as not collected. InterCat generates it: nothing in it was captured, no value in it is evidence
/// about Windows, its records come from a provider named for the demo, and its sessions are published under
/// <see cref="SourceIdentity"/>, which every view of one states. Its sessions are the same, record for record, every time
/// one is made; each investigation file made is an investigation of its own.
/// </summary>
public static class DemoInvestigation
{
    /// <summary>The source identity every demo session is published under: what readers check to say it was generated.</summary>
    public const string SourceIdentity = "intercat-demo-v1";

    /// <summary>What every view of a demo session says, in these words.</summary>
    public const string Disclosure =
        "InterCat generated this demo: nothing in it was captured, and no value in it is evidence about Windows.";

    /// <summary>The demo's provider, named for it, so no record of it claims to be a Windows provider's.</summary>
    public const string ProviderName = "InterCat-Demo";

    /// <summary>The investigation file's name inside the demo's folder.</summary>
    public const string WorkspaceFileName = "demo" + InvestigationWorkspace.Extension;

    /// <summary>The two sessions' folders inside the demo's folder.</summary>
    public const string ClientFolder = "demo-client";

    public const string ServerFolder = "demo-server";

    /// <summary>The demo provider's identity, derived from its name.</summary>
    public static Guid Provider { get; } = Derived("provider");

    /// <summary>When the demo client began recording, by its wall clock: fixed, so every demo's sessions are the same.</summary>
    public static DateTimeOffset Began { get; } = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How much later than the client the demo server began recording, by the same wall clock.</summary>
    public static TimeSpan ServerStartedLater { get; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How long each capture recorded.</summary>
    public static TimeSpan Recorded { get; } = TimeSpan.FromSeconds(60);

    /// <summary>The records the server's capture lost, so the demo shows a coverage gap as a capture states one.</summary>
    public const long ServerLost = 2;

    /// <summary>Whether a session was published as the demo's, by the source identity its generation names.</summary>
    public static bool IsDemo(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Equals(manifest.SourceIdentity, SourceIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the demo into <paramref name="directory"/>, which must not exist or be empty: its two sessions, each
    /// published whole with its checkpoint, and the investigation that holds them, its hosts named, the server aligned
    /// to the client by their wall clocks, and a note on what is there.
    /// </summary>
    public static DemoInvestigationResult Create(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (File.Exists(full) || (System.IO.Directory.Exists(full) && System.IO.Directory.EnumerateFileSystemEntries(full).Any()))
        {
            throw new IOException($"{full} already holds something; the demo is written only into a new or empty folder.");
        }

        System.IO.Directory.CreateDirectory(full);
        string client = Path.Combine(full, ClientFolder);
        string server = Path.Combine(full, ServerFolder);
        Write(client, Host.Client, cancellationToken);
        Write(server, Host.Server, cancellationToken);

        string workspace = Path.Combine(full, WorkspaceFileName);
        DateTimeOffset made = Began + Recorded + TimeSpan.FromMinutes(5);
        InvestigationWorkspace.Create(workspace, made);
        WorkspaceMember clientMember = InvestigationWorkspace.Add(workspace, client, made);
        WorkspaceMember serverMember = InvestigationWorkspace.Add(workspace, server, made);
        InvestigationWorkspace.Alias(workspace, clientMember.HostId, "demo client", made);
        InvestigationWorkspace.Alias(workspace, serverMember.HostId, "demo server", made);
        InvestigationWorkspace.AlignByWallClock(workspace, serverMember.SessionId, clientMember.SessionId,
            synchronizationNanoseconds: 2_000_000, driftPartsPerMillion: 0,
            "The demo's two hosts are stated to keep their wall clocks within 2 ms of each other.", made);
        InvestigationWorkspace.AddNote(workspace, Disclosure + " Its client's browser and sync processes talk to its "
            + "server's api and files processes over TCP, and its server's api asks its db over loopback. The browser also "
            + "sends UDP to a host neither capture recorded. The server began recording 0.75 s after the client and lost "
            + "two records.", at: null, made);
        return new(full, workspace, client, server);
    }

    private enum Host
    {
        Client,
        Server,
    }

    private static void Write(string path, Host host, CancellationToken cancellationToken)
    {
        string name = host == Host.Client ? "client" : "server";
        System.IO.Directory.CreateDirectory(path);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(path), Derived("session|" + name), SourceIdentity);
        var capture = new CaptureId(Derived("capture|" + name));
        var clock = new SourceClockDescriptor(
            new ClockId(Derived("clock|" + name)),
            HostId.Derive("intercat.demo.v1|host|" + name),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            TicksPerSecond,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        ObservationRowV1[] rows = host == Host.Client ? ClientRows() : ServerRows();
        DateTimeOffset began = host == Host.Client ? Began : Began + ServerStartedLater;
        DateTimeOffset stopped = began + Recorded;
        using (DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(
            store,
            new SegmentIdentityV1
            {
                CaptureId = capture,
                ClockId = clock.Id,
                TimestampEncoding = TimestampEncoding.Qpc,
                Derivation = NormalizerContractVersion.V1,
            },
            clock,
            stopped))
        {
            var schemas = new JournalV1SchemaTable();
            Dictionary<ushort, uint> references = [];
            foreach (Descriptor descriptor in Descriptors)
            {
                references[descriptor.EventId] = schemas.Intern(Provider, descriptor.EventId, 0, Fingerprint(descriptor.Name));
            }

            uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
            builder.Journal.WriteSchemas(schemas);
            foreach (ObservationRowV1 row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AddRow(row);
                builder.Journal.Append(Envelope(row, references[row.EventId], policy, capture, clock.Id));
            }

            builder.StageCoverageLedger(Ledger(rows, host == Host.Server ? ServerLost : 0));
            builder.StageClockCalibration(new ClockCalibrationV1
            {
                Contract = ClockCalibrationV1.ContractName,
                CaptureId = capture.Value,
                ClockId = clock.Id.Value,
                BootToken = Derived("boot|" + name),
                WallClock = "the demo's own clock",
                Samples =
                [
                    new() { NativeTicks = 0, Utc = began, AcquisitionUncertaintyNanoseconds = 500 },
                    new() { NativeTicks = Ticks(Recorded), Utc = stopped, AcquisitionUncertaintyNanoseconds = 500 },
                ],
            });
            builder.StageCaptureFinalization(new CaptureFinalizationV1
            {
                Contract = CaptureFinalizationV1.ContractName,
                CaptureId = capture.Value,
                FinalizedUtc = stopped,
                ProvidersStopped = true,
                CallbacksDrained = true,
            });
            _ = builder.Complete(stopped, cancellationToken);
        }

        // Published whole, it opens from its checkpoint and persisted overview, as a finished capture does.
        _ = SessionCheckpoints.Publish(store, stopped, cancellationToken);
    }

    private const long TicksPerSecond = 10_000_000;

    private static long Ticks(TimeSpan time) => time.Ticks;

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);

    private sealed record Descriptor(ushort EventId, string Name, Mechanism Mechanism);

    private static readonly Descriptor[] Descriptors =
    [
        new(1, "process-start", Mechanism.ProcessLifecycle),
        new(2, "process-end", Mechanism.ProcessLifecycle),
        new(10, "tcp-send", Mechanism.Tcp),
        new(11, "tcp-receive", Mechanism.Tcp),
        new(42, "udp-send", Mechanism.Udp),
        new(43, "udp-receive", Mechanism.Udp),
    ];

    // The client: browser.exe and sync.exe on 10.0.0.10, talking to the server's 443 and 8443, and the browser to a name
    // server at 10.0.0.53 neither capture recorded.
    private static ObservationRowV1[] ClientRows()
    {
        var rows = new List<ObservationRowV1>
        {
            Started(0.20, 4_100, @"C:\Program Files\InterCat Demo\browser.exe"),
            Started(0.35, 4_200, @"C:\Program Files\InterCat Demo\sync.exe"),
        };
        for (int request = 0; request < 30; request++)
        {
            double at = 1.0 + (2.0 * request);
            rows.Add(Tcp(at, 4_100, Outbound, 1_200 + (37 * request), "10.0.0.10:50100", "10.0.0.20:443"));
            rows.Add(Tcp(at + 0.030, 4_100, Inbound, 16_000 + (811 * request), "10.0.0.10:50100", "10.0.0.20:443"));
        }

        for (int upload = 0; upload < 9; upload++)
        {
            double at = 2.5 + (6.0 * upload);
            rows.Add(Tcp(at, 4_200, Outbound, 65_536, "10.0.0.10:50200", "10.0.0.20:8443"));
            rows.Add(Tcp(at + 0.120, 4_200, Inbound, 512, "10.0.0.10:50200", "10.0.0.20:8443"));
        }

        for (int lookup = 0; lookup < 6; lookup++)
        {
            double at = 0.9 + (10.0 * lookup);
            rows.Add(Udp(at, 4_100, Outbound, 64, "10.0.0.10:53000", "10.0.0.53:53"));
            rows.Add(Udp(at + 0.008, 4_100, Inbound, 128, "10.0.0.10:53000", "10.0.0.53:53"));
        }

        rows.Add(Ended(58.0, 4_200, 0));
        return Ordered(rows);
    }

    // The server: api.exe, db.exe and files.exe on 10.0.0.20. Each request the client sent reaches it 2 ms later by the
    // shared wall clock; its capture began 0.75 s after the client's, so its session times run that much behind.
    private static ObservationRowV1[] ServerRows()
    {
        double behind = ServerStartedLater.TotalSeconds;
        var rows = new List<ObservationRowV1>
        {
            Started(0.05, 5_100, @"C:\InterCat Demo\api.exe"),
            Started(0.08, 5_200, @"C:\InterCat Demo\db.exe"),
            Started(0.09, 5_300, @"C:\InterCat Demo\files.exe"),
        };
        for (int request = 0; request < 30; request++)
        {
            double at = 1.0 + (2.0 * request) - behind + 0.002;
            rows.Add(Tcp(at, 5_100, Inbound, 1_200 + (37 * request), "10.0.0.20:443", "10.0.0.10:50100"));
            rows.Add(Tcp(at + 0.004, 5_100, Outbound, 300, "127.0.0.1:50300", "127.0.0.1:5432"));
            rows.Add(Tcp(at + 0.0045, 5_200, Inbound, 300, "127.0.0.1:5432", "127.0.0.1:50300"));
            rows.Add(Tcp(at + 0.010, 5_200, Outbound, 4_096, "127.0.0.1:5432", "127.0.0.1:50300"));
            rows.Add(Tcp(at + 0.0105, 5_100, Inbound, 4_096, "127.0.0.1:50300", "127.0.0.1:5432"));
            rows.Add(Tcp(at + 0.026, 5_100, Outbound, 16_000 + (811 * request), "10.0.0.20:443", "10.0.0.10:50100"));
        }

        for (int upload = 0; upload < 9; upload++)
        {
            double at = 2.5 + (6.0 * upload) - behind + 0.002;
            rows.Add(Tcp(at, 5_300, Inbound, 65_536, "10.0.0.20:8443", "10.0.0.10:50200"));
            rows.Add(Tcp(at + 0.116, 5_300, Outbound, 512, "10.0.0.20:8443", "10.0.0.10:50200"));
        }

        return Ordered(rows);
    }

    private const Direction Outbound = Direction.Outbound;

    private const Direction Inbound = Direction.Inbound;

    /// <summary>The rows in time order, numbered in it as a journal numbers what it admits.</summary>
    private static ObservationRowV1[] Ordered(List<ObservationRowV1> rows) =>
        [.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.EventId)
            .Select((row, index) => row with { RawRecordOrdinal = (ulong)(index + 1) })];

    private static ObservationRowV1 Started(double seconds, int processId, string image) =>
        Lifecycle(seconds, 1, ObservationKind.Create, processId, null) with { ResourceName = image };

    private static ObservationRowV1 Ended(double seconds, int processId, long exitCode) =>
        Lifecycle(seconds, 2, ObservationKind.Exit, processId, exitCode);

    private static ObservationRowV1 Lifecycle(double seconds, ushort eventId, ObservationKind kind, int processId, long? exitCode) =>
        Row(seconds, eventId, processId) with
        {
            FactKey = FactKey.Create("process-lifecycle"),
            Mechanism = Mechanism.ProcessLifecycle,
            Layer = ObservationLayer.Lifecycle,
            Kind = kind,
            Direction = Direction.DirectionNotApplicable,
            ByteAvailability = FieldAvailability.NotApplicable,
            StatusCode = exitCode,
            StatusAvailability = exitCode is null ? FieldAvailability.NotApplicable : FieldAvailability.Present,
            MeasurementQuality = QualityLevel.UnknownQuality,
        };

    private static ObservationRowV1 Tcp(double seconds, int processId, Direction direction, long bytes, string local, string remote) =>
        Transfer(seconds, direction == Direction.Outbound ? (ushort)10 : (ushort)11, Mechanism.Tcp, processId, direction, bytes,
            local, remote);

    private static ObservationRowV1 Udp(double seconds, int processId, Direction direction, long bytes, string local, string remote) =>
        Transfer(seconds, direction == Direction.Outbound ? (ushort)42 : (ushort)43, Mechanism.Udp, processId, direction, bytes,
            local, remote);

    private static ObservationRowV1 Transfer(double seconds, ushort eventId, Mechanism mechanism, int processId,
        Direction direction, long bytes, string local, string remote)
    {
        (uint localAddress, ushort localPort) = Endpoint(local);
        (uint remoteAddress, ushort remotePort) = Endpoint(remote);
        bool sent = direction == Direction.Outbound;
        return Row(seconds, eventId, processId) with
        {
            FactKey = FactKey.Create("network-transfer"),
            Mechanism = mechanism,
            Layer = ObservationLayer.Transport,
            Kind = sent ? ObservationKind.Send : ObservationKind.Receive,
            Direction = direction,
            EndpointAddressFamily = 4,
            SourceEndpointAddress = localAddress,
            SourceEndpointPort = localPort,
            DestinationEndpointAddress = remoteAddress,
            DestinationEndpointPort = remotePort,
            ByteValue = bytes,
            ByteDomain = InterCat.Domain.ByteDomain.TransportObserved,
            AccountingSide = sent ? InterCat.Domain.AccountingSide.SendSide : InterCat.Domain.AccountingSide.ReceiveSide,
            MeasurementUnit = InterCat.Domain.MeasurementUnit.Bytes,
            ByteAvailability = FieldAvailability.Present,
            StatusAvailability = FieldAvailability.NotApplicable,
            MeasurementQuality = QualityLevel.Proven,
        };
    }

    /// <summary>What every demo record shares: the demo provider, one stream, the session time its reading gives.</summary>
    private static ObservationRowV1 Row(double seconds, ushort eventId, int processId)
    {
        long ticks = Ticks(seconds);
        Descriptor descriptor = Descriptors.Single(entry => entry.EventId == eventId);
        return new()
        {
            RawStreamId = 1,
            RawSourceEpoch = 1,
            RawRecordOrdinal = 0,
            FactKey = FactKey.Create(descriptor.Name),
            ProviderId = Provider,
            EventId = eventId,
            DescriptorVersion = 0,
            SchemaFingerprint = Fingerprint(descriptor.Name),
            Opcode = 0,
            NativeTicks = ticks,

            // Session time in nanoseconds, as the normalizer converts a reading: the clock's epoch is its zero.
            SessionRelativeTicks = checked(ticks * (SourceClockMath.SessionTicksPerSecond / TicksPerSecond)),
            HeaderProcessId = processId,
            HeaderThreadId = processId + 4,
            ProcessorNumber = 0,
            Mechanism = descriptor.Mechanism,
            Layer = ObservationLayer.Transport,
            Kind = ObservationKind.Send,
            Direction = Direction.DirectionNotApplicable,
            OwnerProcessId = processId,
            ByteAvailability = FieldAvailability.NotApplicable,
            StatusAvailability = FieldAvailability.NotApplicable,
            AttributionQuality = QualityLevel.Proven,
            CorrelationQuality = QualityLevel.UnknownQuality,
            MeasurementQuality = QualityLevel.UnknownQuality,
            TimingQuality = QualityLevel.Proven,
        };
    }

    private static (uint Address, ushort Port) Endpoint(string text)
    {
        int colon = text.LastIndexOf(':');
        byte[] address = IPAddress.Parse(text[..colon]).GetAddressBytes();
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address),
            ushort.Parse(text[(colon + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What its capture collected and delivered: every record it admitted, by descriptor, over the minute it recorded, and
    /// <paramref name="lost"/> records its source session lost, which no descriptor accounts for.
    /// </summary>
    private static CoverageLedgerV1 Ledger(IReadOnlyList<ObservationRowV1> rows, long lost) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.LiveCapture,
                FirstDeliveredNativeTicks = rows.Min(row => row.NativeTicks),
                LastDeliveredNativeTicks = rows.Max(row => row.NativeTicks),
                RecordedFromNativeTicks = 0,
                RecordedToNativeTicks = Ticks(Recorded),
                Collected = [.. Descriptors.Select(descriptor => new CoverageCollectedV1
                {
                    ProviderId = Provider,
                    ProviderName = ProviderName,
                    EventId = descriptor.EventId,
                    Version = 0,
                    Mechanism = descriptor.Mechanism,
                })],
                Deliveries = [.. Descriptors
                    .Select(descriptor => (descriptor, count: rows.Count(row => row.EventId == descriptor.EventId)))
                    .Where(entry => entry.count > 0)
                    .Select(entry => new CoverageDeliveryV1
                    {
                        ProviderId = Provider,
                        EventId = entry.descriptor.EventId,
                        Version = 0,
                        Delivered = entry.count,
                        Admitted = entry.count,
                        Omitted = 0,
                    })],
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

    private static RecordEnvelopeV1 Envelope(ObservationRowV1 row, uint schema, uint policy, CaptureId capture, ClockId clock) => new()
    {
        CaptureId = capture,
        StreamId = row.RawStreamId,
        SourceEpoch = row.RawSourceEpoch,
        RecordOrdinal = row.RawRecordOrdinal,
        Header = new(row.ProviderId, row.EventId, row.DescriptorVersion, 0, 0, row.Opcode, 0, 0, 0, 0, row.HeaderProcessId,
            row.HeaderThreadId, Guid.Empty, Guid.Empty),
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

    private static string Fingerprint(string name) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("intercat.demo.v1|descriptor|" + name)));

    /// <summary>An identity derived from the demo's name for it, as an import's are derived from its file.</summary>
    private static Guid Derived(string name) => HostId.Derive("intercat.demo.v1|" + name).Value;
}
