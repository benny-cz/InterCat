using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What making the discovery demo wrote: its folder, its investigation file and its one session.</summary>
public sealed record DiscoveryDemoResult(string Directory, string WorkspacePath, string Session);

/// <summary>
/// The discovery demo (`contracts/demo-investigation-v1.md` §6, ADR-101): one generated host whose processes share what
/// driverless discovery finds - named synchronization objects, and sections by their handles and by their views - so the
/// graph's hubs, the inspector's resources, their records and the resource-topology basis can be tried without capturing.
/// Like the demo, nothing in it was captured and no value in it is evidence about Windows: its records come from the demo's
/// provider, its session is published under the demo's source identity, which every view states, and it is the same,
/// record for record, every time it is made.
/// </summary>
public static class DiscoveryDemo
{
    /// <summary>The investigation file's name inside the discovery demo's folder.</summary>
    public const string WorkspaceFileName = "discovery" + InvestigationWorkspace.Extension;

    /// <summary>The session's folder inside the discovery demo's folder.</summary>
    public const string SessionFolder = "demo-discovery";

    /// <summary>When the workstation began recording, by its wall clock: fixed, so every discovery demo is the same.</summary>
    public static DateTimeOffset Began { get; } = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    /// <summary>How long its capture recorded.</summary>
    public static TimeSpan Recorded { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Writes the discovery demo into <paramref name="directory"/>, which must not exist or be empty: its session, published
    /// whole with its checkpoint, and the investigation that holds it, its host named and a note on what is there. The
    /// investigation file is made under another name and takes its own last, so a folder that holds it holds the whole demo.
    /// </summary>
    public static DiscoveryDemoResult Create(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (File.Exists(full) || (System.IO.Directory.Exists(full) && System.IO.Directory.EnumerateFileSystemEntries(full).Any()))
        {
            throw new IOException($"{full} already holds something; the demo is written only into a new or empty folder.");
        }

        System.IO.Directory.CreateDirectory(full);
        string session = Path.Combine(full, SessionFolder);
        Write(session, cancellationToken);

        string workspace = Path.Combine(full, WorkspaceFileName);
        string making = workspace + ".making";
        DateTimeOffset made = Began + Recorded + TimeSpan.FromMinutes(5);
        InvestigationWorkspace.Create(making, made);
        WorkspaceMember member = InvestigationWorkspace.Add(making, session, made);
        InvestigationWorkspace.Alias(making, member.HostId, "demo workstation", made);
        InvestigationWorkspace.AddNote(making, DemoInvestigation.Disclosure + " Its editor shares a named event with its "
            + "indexer and a named semaphore and a section with its preview, which maps the section's view beside the editor's; "
            + "the editor alone holds a mutex, and the indexer a view mapped before the capture began. None of it is traffic.",
            at: null, made);
        File.Move(making, workspace);
        return new(full, workspace, session);
    }

    private const long TicksPerSecond = 10_000_000;

    // The object types the stop's rundown names by number, as a capture's type rundown does.
    private const long EventType = 16;
    private const long MutantType = 17;
    private const long SemaphoreType = 18;

    // An image's view carries 0x47 in its extra information's high byte.
    private const long ImageView = 0x47L << 48;

    private const int Editor = 6_100;
    private const int Indexer = 6_200;
    private const int Preview = 6_300;
    private const int SystemProcess = 4;

    private const string IndexReady = @"\Sessions\1\BaseNamedObjects\IndexReady";
    private const string SingleInstance = @"\Sessions\1\BaseNamedObjects\EditorSingleInstance";
    private const string PreviewSlots = @"\BaseNamedObjects\PreviewSlots";
    private const string PreviewFrames = @"\BaseNamedObjects\PreviewFrames";

    private sealed record Descriptor(
        ushort EventId,
        string Name,
        Mechanism Mechanism,
        ObservationLayer Layer,
        ObservationKind Kind,
        Direction Direction,
        byte Opcode);

    private static readonly Descriptor[] Descriptors =
    [
        new(1, "process-start", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Create, Direction.DirectionNotApplicable, 0),
        new(2, "process-end", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Exit, Direction.DirectionNotApplicable, 0),
        new(3, "process-rundown", Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Inventory, Direction.DirectionNotApplicable, 0),
        new(100, "object-handle-created", Mechanism.Synchronization, ObservationLayer.Resource, ObservationKind.Open, Direction.DirectionNotApplicable, 32),
        new(101, "object-handle-duplicated", Mechanism.Synchronization, ObservationLayer.Resource, ObservationKind.Open, Direction.Inbound, 34),
        new(102, "object-handle-closed", Mechanism.Synchronization, ObservationLayer.Resource, ObservationKind.Close, Direction.DirectionNotApplicable, 33),
        new(103, "object-handle-rundown", Mechanism.Synchronization, ObservationLayer.Resource, ObservationKind.Inventory, Direction.DirectionNotApplicable, 39),
        new(104, "object-type-rundown", Mechanism.Synchronization, ObservationLayer.Resource, ObservationKind.Inventory, Direction.DirectionNotApplicable, 37),
        new(110, "section-handle-created", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Open, Direction.DirectionNotApplicable, 32),
        new(111, "section-handle-closed", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Close, Direction.DirectionNotApplicable, 33),
        new(120, "section-view-mapped", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Map, Direction.DirectionNotApplicable, 37),
        new(121, "section-view-unmapped", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Unmap, Direction.DirectionNotApplicable, 38),
        new(122, "section-view-start-rundown", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Inventory, Direction.DirectionNotApplicable, 39),
        new(123, "section-view-stop-rundown", Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Inventory, Direction.DirectionNotApplicable, 40),
    ];

    private sealed record Record(ObservationRowV1 Row, (SourceField Field, long Value)[] Fields);

    private static void Write(string path, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(path);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(path), Derived("session"), DemoInvestigation.SourceIdentity);
        var capture = new CaptureId(Derived("capture"));
        var clock = new SourceClockDescriptor(
            new ClockId(Derived("clock")),
            HostId.Derive("intercat.demo.v1|discovery|host"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            TicksPerSecond,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        Record[] records = Records();
        DateTimeOffset stopped = Began + Recorded;
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
                references[descriptor.EventId] = schemas.Intern(DemoInvestigation.Provider, descriptor.EventId, 0, Fingerprint(descriptor.Name));
            }

            uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
            builder.Journal.WriteSchemas(schemas);
            foreach (Record record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObservationRowV1 row = record.Row;
                builder.AddRow(row);
                foreach ((SourceField field, long value) in record.Fields)
                {
                    builder.AddFieldRow(new SourceFieldRowV1
                    {
                        RawStreamId = row.RawStreamId,
                        RawSourceEpoch = row.RawSourceEpoch,
                        RawRecordOrdinal = row.RawRecordOrdinal,
                        FactKey = row.FactKey,
                        NativeTicks = row.NativeTicks,
                        Field = field,
                        Value = value,
                        Availability = FieldAvailability.Present,
                    });
                }

                builder.Journal.Append(Envelope(row, references[row.EventId], policy, capture, clock.Id));
            }

            builder.StageCoverageLedger(Ledger(records));
            builder.StageClockCalibration(new ClockCalibrationV1
            {
                Contract = ClockCalibrationV1.ContractName,
                CaptureId = capture.Value,
                ClockId = clock.Id.Value,
                BootToken = Derived("boot"),
                WallClock = "the demo's own clock",
                Samples =
                [
                    new() { NativeTicks = 0, Utc = Began, AcquisitionUncertaintyNanoseconds = 500 },
                    new() { NativeTicks = Recorded.Ticks, Utc = stopped, AcquisitionUncertaintyNanoseconds = 500 },
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

    // The workstation: the editor, the indexer - running as the capture began - and the preview, which exits at 50 s.
    private static Record[] Records()
    {
        var records = new List<Record>
        {
            Lifecycle(0.0, 3, Indexer, @"C:\Program Files\InterCat Demo\indexer.exe"),
            Lifecycle(0.10, 1, Editor, @"C:\Program Files\InterCat Demo\editor.exe"),
            Lifecycle(0.20, 1, Preview, @"C:\Program Files\InterCat Demo\preview.exe"),
            Lifecycle(50.0, 2, Preview, null),

            // A named event the editor makes and duplicates into the indexer, which closes it; the editor holds it at the stop.
            Object(1.00, 100, Editor, null, 1, EventType, null),
            Object(1.20, 101, Editor, Indexer, 1, EventType, null),
            Object(30.0, 102, Indexer, null, 1, null, IndexReady),
            Object(59.90, 103, SystemProcess, Editor, 1, null, IndexReady),

            // A mutex the editor alone holds, from its start to the stop.
            Object(0.50, 100, Editor, null, 2, MutantType, null),
            Object(59.90, 103, SystemProcess, Editor, 2, null, SingleInstance),

            // A named semaphore the preview makes and the editor opens by its name; each closes its handle.
            Object(2.00, 100, Preview, null, 3, SemaphoreType, null),
            Object(2.40, 100, Editor, null, 3, SemaphoreType, null),
            Object(49.0, 102, Preview, null, 3, null, PreviewSlots),
            Object(55.0, 102, Editor, null, 3, null, PreviewSlots),

            // The stop's rundown of the types it named, which numbers no object.
            Type(59.95, EventType, "Event"),
            Type(59.95, MutantType, "Mutant"),
            Type(59.95, SemaphoreType, "Semaphore"),

            // A section the editor and the preview each open a handle to by its name, and each close.
            SectionHandle(3.00, 110, Editor, 7, null),
            SectionHandle(3.50, 110, Preview, 7, null),
            SectionHandle(49.50, 111, Preview, 7, PreviewFrames),
            SectionHandle(58.0, 111, Editor, 7, PreviewFrames),

            // Its views, by the number its view gate gave them: the editor's and the preview's 4 MiB each, each unmapped.
            View(3.10, 120, Editor, Editor, 3, 31, 4_194_304, 0),
            View(3.60, 120, Preview, Preview, 3, 32, 4_194_304, 0),
            View(49.40, 121, Preview, Preview, 3, 32, 4_194_304, 0),
            View(58.10, 121, Editor, Editor, 3, 31, 4_194_304, 0),

            // A view the indexer mapped before the capture began, as the start's rundown lists it, unmapped at 20 s.
            View(0.001, 122, SystemProcess, Indexer, 5, 51, 1_048_576, 0),
            View(20.0, 121, Indexer, Indexer, 5, 51, 1_048_576, 0),

            // A module the editor loaded, an image's view, held at the stop.
            View(0.12, 120, Editor, Editor, 9, 91, 131_072, ImageView),
            View(59.90, 123, SystemProcess, Editor, 9, 91, 131_072, ImageView),
        };

        // In time order, numbered in it as a journal numbers what it admits.
        return [.. records.OrderBy(record => record.Row.NativeTicks).ThenBy(record => record.Row.EventId)
            .Select((record, index) => record with { Row = record.Row with { RawRecordOrdinal = (ulong)(index + 1) } })];
    }

    // A process's start, its exit or the start's rundown of one already running, each naming the process it describes.
    private static Record Lifecycle(double seconds, ushort eventId, int processId, string? image) =>
        new(Row(seconds, eventId, processId, processId) with
        {
            FactKey = FactKey.Create("process-lifecycle"),
            ResourceName = image,
            StatusCode = eventId == 2 ? 0 : null,
            StatusAvailability = eventId == 2 ? FieldAvailability.Present : FieldAvailability.NotApplicable,
        }, []);

    // A handle to a synchronization object: its creation and close raised in the process holding it, a duplication raised
    // in the process asking for it and naming the process it made the handle in, and the stop's rundown naming its holder.
    private static Record Object(double seconds, ushort eventId, int raisedBy, int? owner, long number, long? type, string? name)
    {
        (SourceField, long)[] fields = type is { } typed
            ? [(SourceField.SynchronizationObjectNumber, number), (SourceField.ObjectTypeNumber, typed)]
            : [(SourceField.SynchronizationObjectNumber, number)];
        return new(Row(seconds, eventId, raisedBy, owner) with { ResourceName = name }, fields);
    }

    private static Record Type(double seconds, long type, string name) =>
        new(Row(seconds, 104, SystemProcess, null) with { ResourceName = name }, [(SourceField.ObjectTypeNumber, type)]);

    private static Record SectionHandle(double seconds, ushort eventId, int raisedBy, long number, string? name) =>
        new(Row(seconds, eventId, raisedBy, null) with { ResourceName = name }, [(SourceField.SectionObjectNumber, number)]);

    private static Record View(double seconds, ushort eventId, int raisedBy, int owner, long section, long view, long size, long information) =>
        new(Row(seconds, eventId, raisedBy, owner),
        [
            (SourceField.SectionNumber, section),
            (SourceField.SectionViewNumber, view),
            (SourceField.ViewSize, size),
            (SourceField.ViewInformation, information),
        ]);

    /// <summary>What every record shares: the demo provider, one stream, its descriptor's facts, the session time its reading gives.</summary>
    private static ObservationRowV1 Row(double seconds, ushort eventId, int raisedBy, int? owner)
    {
        long ticks = (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
        Descriptor descriptor = Descriptors.Single(entry => entry.EventId == eventId);
        return new()
        {
            RawStreamId = 1,
            RawSourceEpoch = 1,
            RawRecordOrdinal = 0,
            FactKey = FactKey.Create(descriptor.Name),
            ProviderId = DemoInvestigation.Provider,
            EventId = eventId,
            DescriptorVersion = 0,
            SchemaFingerprint = Fingerprint(descriptor.Name),
            Opcode = descriptor.Opcode,
            NativeTicks = ticks,

            // Session time in nanoseconds, as the normalizer converts a reading: the clock's epoch is its zero.
            SessionRelativeTicks = checked(ticks * (SourceClockMath.SessionTicksPerSecond / TicksPerSecond)),
            HeaderProcessId = raisedBy,
            HeaderThreadId = raisedBy + 4,
            ProcessorNumber = 0,
            OwnerProcessId = owner,
            Mechanism = descriptor.Mechanism,
            Layer = descriptor.Layer,
            Kind = descriptor.Kind,
            Direction = descriptor.Direction,
            ByteAvailability = FieldAvailability.NotApplicable,
            StatusAvailability = FieldAvailability.NotApplicable,
            AttributionQuality = QualityLevel.Proven,
            CorrelationQuality = QualityLevel.UnknownQuality,
            MeasurementQuality = QualityLevel.UnknownQuality,
            TimingQuality = QualityLevel.Proven,
        };
    }

    /// <summary>What its capture collected and delivered: every record it admitted, by descriptor, over its minute, none lost.</summary>
    private static CoverageLedgerV1 Ledger(IReadOnlyList<Record> records) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.LiveCapture,
                FirstDeliveredNativeTicks = records.Min(record => record.Row.NativeTicks),
                LastDeliveredNativeTicks = records.Max(record => record.Row.NativeTicks),
                RecordedFromNativeTicks = 0,
                RecordedToNativeTicks = Recorded.Ticks,
                Collected = [.. Descriptors.Select(descriptor => new CoverageCollectedV1
                {
                    ProviderId = DemoInvestigation.Provider,
                    ProviderName = DemoInvestigation.ProviderName,
                    EventId = descriptor.EventId,
                    Version = 0,
                    Mechanism = descriptor.Mechanism,
                })],
                Deliveries = [.. Descriptors
                    .Select(descriptor => (descriptor, count: records.Count(record => record.Row.EventId == descriptor.EventId)))
                    .Where(entry => entry.count > 0)
                    .Select(entry => new CoverageDeliveryV1
                    {
                        ProviderId = DemoInvestigation.Provider,
                        EventId = entry.descriptor.EventId,
                        Version = 0,
                        Delivered = entry.count,
                        Admitted = entry.count,
                        Omitted = 0,
                    })],
                Losses =
                [
                    new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
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
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("intercat.demo.v1|discovery|descriptor|" + name)));

    /// <summary>An identity derived from the discovery demo's name for it, as an import's are derived from its file.</summary>
    private static Guid Derived(string name) => HostId.Derive("intercat.demo.v1|discovery|" + name).Value;
}
