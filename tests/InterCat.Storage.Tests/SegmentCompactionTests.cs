using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §20.1's compaction targets: a live recording's small publications are coalesced into bounded segments, and every
/// row survives exactly, with the evidence and its boundary untouched (ADR-026).
/// </summary>
public sealed class SegmentCompactionTests
{
    private static readonly Guid Session = Guid.Parse("d5e6f7a8-b9c0-4d1e-8f2a-3b4c5d6e7f80");
    private static readonly CaptureId Capture = new(Guid.Parse("c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f"));
    private static readonly ClockId Clock = new(Guid.Parse("33334444-5555-4666-8777-888899990000"));
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly DateTimeOffset Committed = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "I15: compaction coalesces small publications into bounded segments and keeps every row")]
    public void CompactionCoalescesSmallPublications()
    {
        using var session = new TemporarySession();
        for (int chunk = 0; chunk < 5; chunk++)
        {
            PublishChunk(session.Store, firstOrdinal: 1 + ((ulong)chunk * 10), records: 10);
        }

        SessionManifestV1 before = session.Store.Current!;
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) recorded = RowsOf(session.Store, before);
        Assert.Equal((50, 50), (recorded.Rows.Length, recorded.Fields.Length));

        CompactionPlan plan = SegmentCompaction.Plan(session.Store);
        Assert.Equal((5, 5, 5, false), (plan.Units.Count, plan.SmallUnits, plan.ObservationSegments, plan.Due));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], Assert.Single(plan.Runs).Select(unit => unit.Generation));
        Assert.Equal((50L, 50L), (plan.CoalescedRows, plan.CoalescedFieldRows));

        CompactionResult result = SegmentCompaction.Compact(session.Store, Committed)!;
        SessionManifestV1 after = result.Generation.Manifest;
        Assert.Equal(6, after.Generation);

        // One observation and one field segment hold exactly the rows five publications held.
        Assert.Single(SessionSegments.Names(after));
        Assert.Single(SessionSegments.FieldNames(after));
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) compacted = RowsOf(session.Store, after);
        Assert.Equal(recorded.Rows, compacted.Rows);
        Assert.Equal(recorded.Fields, compacted.Fields);

        // Only derived files changed: the journals, the boundary and the evidence they describe are carried as they are.
        Assert.Equal(EvidenceOf(before), EvidenceOf(after));
        Assert.Equal(before.Boundary, after.Boundary);
        RetentionRecord record = Assert.IsType<RetentionRecord>(after.Retention);
        Assert.Equal(RetentionExtentKind.DerivedFiles, record.Kind);
        Assert.Equal(plan.CoalescedFiles, record.ReleasedFiles.Count);
        Assert.Contains("every one kept", record.Reason, StringComparison.Ordinal);
        Assert.All(result.Retention.RemovedFiles, name => Assert.False(File.Exists(Path.Combine(session.Path, name))));
        Assert.Equal(record.ReleasedFiles.Order(StringComparer.Ordinal), result.Retention.RemovedFiles.Order(StringComparer.Ordinal));

        SessionStore reopened = session.Reopen();
        Assert.Equal(after.Generation, reopened.Current!.Generation);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.False(SegmentCompaction.Plan(reopened).CompactsAnything);
        Assert.Null(SegmentCompaction.Compact(reopened, Committed));
    }

    [Fact(DisplayName = "I15: compaction coalesces only runs of small units, and a live budget takes the oldest run in steps")]
    public void CompactionKeepsLargeUnitsAndBoundsALiveStep()
    {
        using var session = new TemporarySession();
        var options = new CompactionOptions { TargetRows = 20, SmallUnitsBeforeCompaction = 4 };
        PublishChunk(session.Store, 1, 10);
        PublishChunk(session.Store, 11, 10);
        PublishChunk(session.Store, 21, 10);
        PublishChunk(session.Store, 31, 25);
        PublishChunk(session.Store, 56, 10);
        PublishChunk(session.Store, 66, 10);
        (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) recorded = RowsOf(session.Store, session.Store.Current!);

        // Four small units around one that already reaches the target: two runs, and the large unit stays as it is.
        CompactionPlan plan = SegmentCompaction.Plan(session.Store, options);
        Assert.Equal((6, 5, true), (plan.Units.Count, plan.SmallUnits, plan.Due));
        Assert.Equal(
            [[1L, 2L, 3L], [5L, 6L]],
            plan.Runs.Select(run => run.Select(unit => unit.Generation).ToArray()));

        // A live step rewrites a bounded number of rows: the oldest run, cut at the budget.
        CompactionPlan step = SegmentCompaction.Plan(session.Store, options with { RowBudget = 15 });
        Assert.Equal([1L, 2L], Assert.Single(step.Runs).Select(unit => unit.Generation));

        // A reader's lease keeps what it acquired: the replaced files stay until it lets go (I18).
        using (EvidenceLease reader = session.Store.AcquireLease())
        {
            CompactionResult result = SegmentCompaction.Compact(session.Store, Committed, options)!;
            Assert.True(result.Retention.AwaitingRelease);
            Assert.All(result.Retention.HeldByLease, name => Assert.True(File.Exists(Path.Combine(session.Path, name))));

            // Each run became segments of its own, so no output spans the large unit's time.
            SessionManifestV1 after = result.Generation.Manifest;
            Assert.Equal(3, SessionSegments.Names(after).Count);
            Assert.Contains("seg-0000000004-0000.icats", SessionSegments.Names(after));
            Assert.Equal(recorded.Rows, RowsOf(session.Store, after).Rows);
            Assert.Equal(recorded.Fields, RowsOf(session.Store, after).Fields);
        }
    }

    [Fact(DisplayName = "I15: a compaction replaces derived files only, never the evidence or what describes it")]
    public void CompactionReplacesDerivedFilesOnly()
    {
        using var session = new TemporarySession();
        PublishChunk(session.Store, 1, 10);
        SessionManifestV1 manifest = session.Store.Current!;
        string journal = manifest.Boundary.JournalName;
        using StoreStagingFile staged = session.Store.Stage("seg-0000000002-0000.icats", StoreDependencyKind.Segment);
        staged.Content.Write("not a segment"u8);
        _ = staged.Complete();

        Assert.Throws<ArgumentException>(() => session.Store.CommitCompaction(
            [staged], [journal], "compact the journal", manifest.Generation, Committed));
        Assert.Throws<InvalidOperationException>(() => session.Store.CommitCompaction(
            [staged], ["seg-0000000001-0000.icats"], "stale", manifest.Generation - 1, Committed));
        Assert.Equal(manifest.Generation, session.Store.Current!.Generation);
    }

    /// <summary>Publishes one live-recording chunk: its journal, one row and one source field per record.</summary>
    private static void PublishChunk(SessionStore store, ulong firstOrdinal, int records)
    {
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(store, Identity, TestClock, Committed);
        var schemas = new JournalV1SchemaTable();
        uint schema = schemas.Intern(Provider, 10, 0, "sha256:" + new string('a', 64));
        uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
        builder.Journal.WriteSchemas(schemas);
        for (int index = 0; index < records; index++)
        {
            ulong ordinal = firstOrdinal + (ulong)index;
            long ticks = 100L * (long)ordinal;
            ObservationRowV1 row = Row(ticks, ordinal);
            builder.AddRow(row);
            builder.AddFieldRow(new SourceFieldRowV1
            {
                RawStreamId = row.RawStreamId,
                RawSourceEpoch = row.RawSourceEpoch,
                RawRecordOrdinal = row.RawRecordOrdinal,
                FactKey = row.FactKey,
                NativeTicks = row.NativeTicks,
                Field = SourceField.ConnectionId,
                Value = 0x7000 + (long)ordinal,
                Availability = FieldAvailability.Present,
            });
            builder.Journal.Append(Envelope(ticks, ordinal, schema, policy));
        }

        _ = builder.Complete(Committed);
    }

    /// <summary>A generation's rows and source fields, in raw-record order.</summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) RowsOf(SessionStore store, SessionManifestV1 manifest) =>
    (
        [
            .. SessionSegments.Names(manifest)
                .Select(name => SessionSegments.Open(store.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.Row))
                .OrderBy(row => row.RawRecordOrdinal),
        ],
        [
            .. SessionSegments.FieldNames(manifest)
                .Select(name => SessionSegments.Open(store.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow))
                .OrderBy(field => field.RawRecordOrdinal),
        ]);

    /// <summary>What a compaction must carry unchanged: every dependency that is not a segment or a dictionary.</summary>
    private static StoreDependency[] EvidenceOf(SessionManifestV1 manifest) =>
    [
        .. manifest.Dependencies
            .Where(dependency => dependency.Kind is not (StoreDependencyKind.Segment or StoreDependencyKind.Dictionary))
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal),
    ];

    private static ObservationRowV1 Row(long ticks, ulong ordinal) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        JournalRecordIndex = ordinal - 1,
        FactKey = FactKey.Create("network-transfer"),
        ProviderId = Provider,
        EventId = 10,
        DescriptorVersion = 0,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = 10,
        NativeTicks = ticks,
        HeaderProcessId = 1_234,
        HeaderThreadId = 5_678,
        ProcessorNumber = 1,
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        ByteValue = ticks,
        ByteDomain = ByteDomain.TransportObserved,
        AccountingSide = AccountingSide.SendSide,
        MeasurementUnit = MeasurementUnit.Bytes,
        ByteAvailability = FieldAvailability.Present,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.Proven,
        TimingQuality = QualityLevel.Proven,
    };

    private static RecordEnvelopeV1 Envelope(long ticks, ulong ordinal, uint schema, uint policy) => new()
    {
        CaptureId = Capture,
        StreamId = 1,
        SourceEpoch = 1,
        RecordOrdinal = ordinal,
        Header = new(Provider, 10, 0, 0, 0, 10, 0, 0, 0, 0, 1_234, 5_678, Guid.Empty, Guid.Empty),
        BufferContext = new(1, 0),
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = ticks,
        PointerSize = 8,
        SchemaReference = schema,
        AdmissionPolicyReference = policy,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = BodyV1.None,
    };

    private static SourceClockDescriptor TestClock { get; } = new(
        Clock,
        HostId.Derive("segment-compaction-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        0,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    private static SegmentIdentityV1 Identity { get; } = new()
    {
        CaptureId = Capture,
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        Derivation = NormalizerContractVersion.V1,
    };

    private sealed class TemporarySession : IDisposable
    {
        public TemporarySession()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterCat.Storage.Tests.Compaction",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "compaction-tests");
        }

        public string Path { get; }

        public SessionStore Store { get; private set; }

        public SessionStore Reopen()
        {
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "compaction-tests");
            return Store;
        }

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
}
