using System.Buffers.Binary;
using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// The `observation-v1` segment format. The tests are written against the refusals as much as the round
/// trips: a derived store that reads a damaged or dishonest segment is worse than one that refuses it,
/// because every count above it inherits the damage silently.
/// </summary>
public sealed class SegmentV1Tests
{
    private static readonly CaptureId Capture = new(Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"));
    private static readonly ClockId Clock = new(Guid.Parse("11112222-3333-4444-8555-666677778888"));
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

    [Fact(DisplayName = "R1: a published segment carries source facts only, never a resolved identity")]
    public void ASegmentCarriesSourceFactsOnly()
    {
        // R1 is a property of the column set, not of a code path: if no column can hold a process instance,
        // a channel, a relation or a derived metric, no correlation revision can rewrite evidence by writing
        // one. Those live in their own dependencies, which is what makes an earlier snapshot reproducible.
        IReadOnlyList<SegmentColumnId> declared = [.. SegmentFormatV1.ObservationColumns.Select(column => column.Id)];

        Assert.Equal(39, declared.Count);
        Assert.Equal(declared.Distinct(), declared);
        foreach (SegmentColumnId id in declared)
        {
            string name = id.ToString();
            Assert.DoesNotContain("Instance", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Relation", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Channel", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Binding", name, StringComparison.Ordinal);
        }

        // The locator is what ties a derived row back to the evidence it came from, and it is carried in the
        // row so it survives a later compaction that changes which segment a row lives in (§20.1).
        Assert.Contains(SegmentColumnId.RawStreamId, declared);
        Assert.Contains(SegmentColumnId.RawSourceEpoch, declared);
        Assert.Contains(SegmentColumnId.RawRecordOrdinal, declared);
        Assert.Contains(SegmentColumnId.JournalRecordIndex, declared);
    }

    [Fact(DisplayName = "R1: a published segment is immutable, and a later generation reads the same bytes")]
    public void APublishedSegmentIsImmutable()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, [Row(100, bytes: 64), Row(200, bytes: 32)]);
        string name = first.Segments[0].Name;
        ObservationRowV1 before = Read(session.Store, first.Manifest, name, 0);

        DerivedGenerationResult second = Publish(session.Store, [Row(300, bytes: 16)]);

        Assert.Equal(2, second.Manifest.Generation);
        Assert.NotEqual(name, second.Segments[0].Name);
        Assert.Equal(before, Read(session.Store, second.Manifest, name, 0));
    }

    [Fact(DisplayName = "R2: a measurement carries its value or null, its unit, its domain and its side")]
    public void AMeasurementCarriesItsLabels()
    {
        SegmentReaderV1 segment = Build(
            [
                Row(100, bytes: 1_024),
                Row(200, bytes: null, availability: FieldAvailability.NotExposed),
                Row(300, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false),
            ]);

        ObservationRowV1 measured = segment.Row(0);
        Assert.Equal(1_024, measured.ByteValue);
        Assert.Equal(ByteDomain.TransportObserved, measured.ByteDomain);
        Assert.Equal(AccountingSide.SendSide, measured.AccountingSide);
        Assert.Equal(MeasurementUnit.Bytes, measured.MeasurementUnit);
        Assert.Equal(FieldAvailability.Present, measured.ByteAvailability);

        // A declared slot with no value keeps its labels. That is what lets it be counted against a defined
        // denominator instead of disappearing from the arithmetic.
        ObservationRowV1 unknown = segment.Row(1);
        Assert.Null(unknown.ByteValue);
        Assert.Equal(ByteDomain.TransportObserved, unknown.ByteDomain);
        Assert.Equal(FieldAvailability.NotExposed, unknown.ByteAvailability);

        // A descriptor with no byte field at all carries no labels, because labelling a slot that does not
        // exist would put it in that denominator.
        ObservationRowV1 inapplicable = segment.Row(2);
        Assert.Null(inapplicable.ByteValue);
        Assert.Null(inapplicable.ByteDomain);
        Assert.Null(inapplicable.AccountingSide);
        Assert.Null(inapplicable.MeasurementUnit);
        Assert.Equal(FieldAvailability.NotApplicable, inapplicable.ByteAvailability);
    }

    [Fact(DisplayName = "R3: a null measurement always states why, and a present one cannot be null")]
    public void ANullMeasurementStatesWhy()
    {
        Assert.Contains(
            "`Present` is not a reason",
            Row(100, bytes: null, availability: FieldAvailability.Present).Validate(),
            StringComparison.Ordinal);
        Assert.Contains(
            "also reports it as",
            Row(100, bytes: 8, availability: FieldAvailability.NotExposed).Validate(),
            StringComparison.Ordinal);
        Assert.Contains(
            "labelling a slot that does not exist",
            Row(100, bytes: null, availability: FieldAvailability.NotApplicable).Validate(),
            StringComparison.Ordinal);
        Assert.Contains(
            "still carries that slot's domain",
            Row(100, bytes: null, availability: FieldAvailability.EventLost, declareSlot: false).Validate(),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I6: a byte sum covers one domain and one side, and counts everything it left out")]
    public void AByteSumCoversOneDomainAndOneSide()
    {
        SegmentReaderV1 segment = Build(
            [
                Row(100, bytes: 100, side: AccountingSide.SendSide),
                Row(200, bytes: 40, side: AccountingSide.SendSide),
                Row(300, bytes: 100, side: AccountingSide.ReceiveSide),
                Row(400, bytes: 4_096, domain: ByteDomain.RequestedIo, side: AccountingSide.SendSide),
                Row(500, bytes: null, availability: FieldAvailability.EventLost, side: AccountingSide.SendSide),
                Row(600, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false),
            ]);

        ByteSumResult sent = SegmentMeasurement.SumBytes(
            segment,
            new() { Domain = ByteDomain.TransportObserved, Side = AccountingSide.SendSide });

        // The requested-I/O row is 4,096 bytes of a different quantity. It is excluded and counted, never
        // added: 140 and 4,236 are both defensible-looking numbers and only one of them means anything.
        Assert.Equal(140, sent.TotalBytes);
        Assert.Equal(2, sent.KnownContributions);
        Assert.Equal(1, sent.UnknownContributions);
        Assert.Equal(1, sent.ExcludedOtherDomain);
        Assert.Equal(1, sent.ExcludedOtherSide);
        Assert.Equal(1, sent.ExcludedNoDeclaredSlot);
        Assert.Equal(MeasurementUnit.Bytes, sent.Unit);
        Assert.Equal(FieldAvailability.EventLost, Assert.Single(sent.UnknownReasons).Key);
        Assert.Equal(2.0 / 3, sent.MeasurementAvailability!.Value, 6);
        Assert.False(sent.IsComplete);

        ByteSumResult requested = SegmentMeasurement.SumBytes(
            segment,
            new() { Domain = ByteDomain.RequestedIo, Side = AccountingSide.SendSide });

        Assert.Equal(4_096, requested.TotalBytes);
        Assert.True(requested.IsComplete);
    }

    [Fact(DisplayName = "I11: an application-layer generation changes no transport-layer metric")]
    public void AHigherLayerAnnotationChangesNoTransportMetric()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult transport = Publish(
            session.Store,
            [Row(100, bytes: 100), Row(200, bytes: 40)]);
        string transportSegment = transport.Segments[0].Name;
        ByteSumResult before = Sum(session.Store, transport.Manifest, transportSegment);

        // A later generation adds application-layer evidence over the same capture. It is a new immutable
        // segment, so the transport sum is not merely unchanged by convention - there is nothing the second
        // generation could have written that would change it.
        DerivedGenerationResult application = Publish(
            session.Store,
            [
                Row(150, bytes: 140, layer: ObservationLayer.Application, mechanism: Mechanism.Rpc),
                Row(250, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false,
                    layer: ObservationLayer.Application, mechanism: Mechanism.Rpc),
            ]);

        // The comparison is field by field on purpose: what must be unchanged is every number a reader would
        // show, not an object identity.
        ByteSumResult after = Sum(session.Store, application.Manifest, transportSegment);
        Assert.Equal(140, before.TotalBytes);
        Assert.Equal(before.TotalBytes, after.TotalBytes);
        Assert.Equal(before.KnownContributions, after.KnownContributions);
        Assert.Equal(before.UnknownContributions, after.UnknownContributions);
        Assert.Equal(before.ExcludedOtherDomain, after.ExcludedOtherDomain);
        Assert.Equal(before.ExcludedOtherSide, after.ExcludedOtherSide);
        Assert.Equal(before.ExcludedNoDeclaredSlot, after.ExcludedNoDeclaredSlot);
        Assert.Equal(before.ExcludedByProjection, after.ExcludedByProjection);
        Assert.Equal(before.Unit, after.Unit);
        Assert.Equal(before.MeasurementAvailability, after.MeasurementAvailability);

        ByteSumResult applicationSum = Sum(
            session.Store,
            application.Manifest,
            application.Segments[0].Name,
            ObservationLayer.Application);
        Assert.Equal(140, applicationSum.TotalBytes);

        // Projected onto the transport layer, the application generation contributes nothing at all - not a
        // zero it summed, but no contribution to sum.
        ByteSumResult transportOfApplication = Sum(
            session.Store,
            application.Manifest,
            application.Segments[0].Name,
            ObservationLayer.Transport);
        Assert.Equal(0, transportOfApplication.KnownContributions);
        Assert.Equal(2, transportOfApplication.ExcludedByProjection);
        Assert.Null(transportOfApplication.MeasurementAvailability);
    }

    [Fact(DisplayName = "P11: quality stays four separate dimensions in the stored row")]
    public void QualityStaysFourDimensions()
    {
        SegmentReaderV1 segment = Build([Row(100, bytes: 64)]);
        ObservationRowV1 row = segment.Row(0);

        Assert.Equal(QualityLevel.Proven, row.AttributionQuality);
        Assert.Equal(QualityLevel.UnknownQuality, row.CorrelationQuality);
        Assert.Equal(QualityLevel.Proven, row.MeasurementQuality);
        Assert.Equal(QualityLevel.Qualified, row.TimingQuality);
        Assert.Equal(
            4,
            SegmentFormatV1.ObservationColumns.Count(column => column.Id.ToString().EndsWith("Quality", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "I15: a segment round-trips every column, including its nulls and its names")]
    public void ASegmentRoundTripsEveryColumn()
    {
        ObservationRowV1 full = Row(1_000, bytes: 512) with
        {
            JournalRecordIndex = 7,
            SessionRelativeTicks = -4_096,
            ActivityId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            RelatedActivityId = Guid.Parse("11111111-2222-4333-8444-555555555555"),
            OwnerProcessId = 4_242,
            ResourceName = @"\Device\NamedPipe\intercat-fixture",
            SourceIdentifier = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003"),
            EndpointAddressFamily = 4,
            SourceEndpointAddress = 0x7F000001,
            SourceEndpointPort = 52_100,
            DestinationEndpointAddress = 0x7F000001,
            DestinationEndpointPort = 443,
            StatusCode = 3_221_225_524,
            StatusAvailability = FieldAvailability.Present,
            Markers = SegmentRowMarkers.ResourceNameTruncated | SegmentRowMarkers.ExtendedDataRequested,
        };
        ObservationRowV1 sparse = Row(2_000, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false);

        SegmentReaderV1 segment = Build([full, sparse]);

        Assert.Equal(full, segment.Row(0));
        Assert.Equal(sparse, segment.Row(1));
        Assert.Equal(Capture, segment.CaptureId);
        Assert.Equal(Clock, segment.ClockId);
        Assert.Equal(TimestampEncoding.Qpc, segment.TimestampEncoding);
        Assert.Equal(NormalizerContractVersion.V1, segment.Derivation);
        Assert.Equal(SegmentTableId.ObservationV1, segment.Table);
        Assert.Equal(1_000, segment.MinNativeTicks);
        Assert.Equal(2_000, segment.MaxNativeTicks);
        Assert.Equal(
            new ObservationId(new(Capture, 1, 1, full.RawRecordOrdinal), NormalizerContractVersion.V1, full.FactKey),
            segment.ObservationIdOf(0));
    }

    [Fact(DisplayName = "I15: a segment's bytes are a function of its rows, not of their arrival order")]
    public void ASegmentIsAFunctionOfItsRows()
    {
        ObservationRowV1[] rows =
        [
            Row(300, bytes: 3, ordinal: 3) with { ResourceName = "gamma" },
            Row(100, bytes: 1, ordinal: 1) with { ResourceName = "alpha" },
            Row(200, bytes: 2, ordinal: 2) with { ResourceName = "beta" },
        ];

        byte[] forward = Encode(rows);
        byte[] reversed = Encode([.. rows.Reverse()]);

        Assert.Equal(forward, reversed);
        SegmentReaderV1 segment = Build(rows);
        Assert.Equal([100, 200, 300], Enumerable.Range(0, 3).Select(row => segment.Row(row).NativeTicks));
        Assert.Equal(["alpha", "beta", "gamma"], Enumerable.Range(0, 3).Select(row => segment.Row(row).ResourceName));
    }

    [Fact(DisplayName = "I15: a null bitmap and its availability counters describe the same rows")]
    public void AvailabilityCountersMatchTheBitmap()
    {
        SegmentReaderV1 segment = Build(
            [
                Row(100, bytes: 1) with { OwnerProcessId = 10 },
                Row(200, bytes: 2),
                Row(300, bytes: 3) with { OwnerProcessId = 30 },
            ]);

        SegmentColumnDescriptor owner = segment.Column(SegmentColumnId.OwnerProcessId)!;
        Assert.Equal(2, owner.KnownCount);
        Assert.Equal(1, owner.UnknownCount);
        Assert.True(segment.HasValue(SegmentColumnId.OwnerProcessId, 0));
        Assert.False(segment.HasValue(SegmentColumnId.OwnerProcessId, 1));
        Assert.Null(segment.Row(1).OwnerProcessId);

        SegmentColumnDescriptor ticks = segment.Column(SegmentColumnId.NativeTicks)!;
        Assert.False(ticks.Nullable);
        Assert.Equal(3, ticks.KnownCount);
        Assert.Equal(0, ticks.NullBitmapLength);
    }

    [Fact(DisplayName = "I15: time blocks cover every row and state the interval their rows actually hold")]
    public void TimeBlocksCoverEveryRow()
    {
        ObservationRowV1[] rows = [.. Enumerable.Range(0, 20_000)
            .Select(index => Row(1_000 + index, bytes: index, ordinal: (ulong)index + 1))];

        SegmentReaderV1 segment = Build(rows);

        Assert.Equal(3, segment.TimeBlocks.Count);
        Assert.Equal(20_000, segment.TimeBlocks.Sum(block => block.RowCount));
        Assert.Equal(0, segment.TimeBlocks[0].FirstRow);
        Assert.Equal(SegmentFormatV1.RowsPerTimeBlock, segment.TimeBlocks[0].RowCount);
        Assert.Equal(1_000, segment.TimeBlocks[0].MinNativeTicks);
        Assert.Equal(1_000 + SegmentFormatV1.RowsPerTimeBlock - 1, segment.TimeBlocks[0].MaxNativeTicks);
        Assert.Equal(20_999, segment.TimeBlocks[^1].MaxNativeTicks);
    }

    [Fact(DisplayName = "I15: a dictionary is sorted and unique, and a column resolves through it")]
    public void ADictionaryIsSortedAndUnique()
    {
        SegmentDictionaryV1 dictionary = SegmentDictionaryV1.Create(
            4,
            SegmentDictionaryKind.Utf8Text,
            ["zeta", "alpha", "zeta", "Alpha", "ünicode"]);

        Assert.Equal(["Alpha", "alpha", "zeta", "ünicode"], dictionary.Entries);
        Assert.True(dictionary.TryGetCode("zeta", out uint code));
        Assert.Equal(2u, code);
        Assert.Equal("zeta", dictionary[2]);

        SegmentDictionaryV1 decoded = SegmentDictionaryV1.Decode(dictionary.Encode());
        Assert.Equal(dictionary.Entries, decoded.Entries);
        Assert.Equal(4, decoded.DictionaryId);
        Assert.Equal(SegmentDictionaryKind.Utf8Text, decoded.Kind);
    }

    [Fact(DisplayName = "I15: a text column past the dictionary budget falls back to the variable chunk")]
    public void ATextColumnPastTheBudgetFallsBackToAChunk()
    {
        int rows = SegmentFormatV1.MaximumDictionaryEntries + 1;
        ObservationRowV1[] distinct = [.. Enumerable.Range(0, rows).Select(index =>
            Row(1_000 + index, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false, ordinal: (ulong)index + 1)
                with { ResourceName = index.ToString("D8", CultureInfo.InvariantCulture) })];

        SegmentReaderV1 segment = Build(distinct);

        SegmentColumnDescriptor name = segment.Column(SegmentColumnId.ResourceName)!;
        Assert.Equal(SegmentColumnEncoding.VariableReference, name.Encoding);
        Assert.Equal(0, name.DictionaryId);
        Assert.Equal("00000000", segment.Row(0).ResourceName);
        Assert.Equal((rows - 1).ToString("D8", CultureInfo.InvariantCulture), segment.Row(rows - 1).ResourceName);

        // The schema dictionary is still a dictionary: the fallback is per column, not per segment.
        Assert.Equal(SegmentColumnEncoding.Dictionary, segment.Column(SegmentColumnId.SchemaCode)!.Encoding);
    }

    [Fact(DisplayName = "R8: staged UTF-8 resource names count against the segment's flush budget")]
    public void StagedTextCountsTowardTheFlushBudget()
    {
        var writer = new SegmentWriterV1(Identity, 0);
        long before = writer.StagedBytes;
        string name = new('é', 1_000);
        writer.Add(Row(100, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false)
            with { ResourceName = name });

        Assert.True(writer.StagedBytes - before >= System.Text.Encoding.UTF8.GetByteCount(name));
    }

    [Theory(DisplayName = "I15: a damaged or dishonest segment is refused, never read at a guess")]
    [InlineData(0, "not a segment-v1 segment")]
    [InlineData(8, "format major")]
    [InlineData(12, "requires features")]
    [InlineData(68, "header fails its checksum")]
    public void ADamagedSegmentIsRefused(int offset, string expected)
    {
        byte[] segment = Encode([Row(100, bytes: 1), Row(200, bytes: 2)]);
        segment[offset] ^= 0x5A;

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => SegmentReaderV1.Open(segment));

        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a segment whose trailing digest does not cover its bytes is refused")]
    public void ATamperedSegmentIsRefused()
    {
        (byte[] bytes, IReadOnlyList<SegmentDictionaryV1> dictionaries) = EncodeWithDictionaries(
            [Row(100, bytes: 1), Row(200, bytes: 2)]);
        int valueOffset = SegmentReaderV1.Open(bytes, dictionaries).Column(SegmentColumnId.ByteValue)!.ValueOffset;
        bytes[valueOffset] ^= 0xFF;

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            SegmentReaderV1.Open(bytes, dictionaries));

        Assert.Contains("trailing digest", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a column whose checksum does not match its bytes is refused when it is read")]
    public void ATamperedColumnIsRefusedOnRead()
    {
        (byte[] bytes, IReadOnlyList<SegmentDictionaryV1> dictionaries) = EncodeWithDictionaries(
            [Row(100, bytes: 1), Row(200, bytes: 2)]);
        SegmentColumnDescriptor column = SegmentReaderV1.Open(bytes, dictionaries).Column(SegmentColumnId.ByteValue)!;

        // The bytes and the trailing digest are both rewritten, so the file passes as a whole and only the
        // column's own checksum disagrees. That is the case a whole-file digest cannot catch on its own.
        bytes[column.ValueOffset] ^= 0xFF;
        System.Security.Cryptography.SHA256.HashData(
            bytes.AsSpan(0, bytes.Length - SegmentFormatV1.TrailerLength),
            bytes.AsSpan(bytes.Length - SegmentFormatV1.TrailerLength));

        SegmentReaderV1 segment = SegmentReaderV1.Open(bytes, dictionaries);
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            segment.Row(0));

        Assert.Contains("fails its checksum", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: an empty segment is never published, because absence is not data")]
    public void AnEmptySegmentIsNeverPublished()
    {
        var writer = new SegmentWriterV1(Identity, 0);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => writer.Build());

        Assert.Contains("absence be read as data", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I2: two rows with one observation identity are refused rather than counted twice")]
    public void TwoRowsWithOneIdentityAreRefused()
    {
        var writer = new SegmentWriterV1(Identity, 0);
        writer.Add(Row(100, bytes: 1, ordinal: 9));
        writer.Add(Row(100, bytes: 1, ordinal: 9));

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => writer.Build());

        Assert.Contains("counted twice", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I15: a generation publishes its journal, its dictionaries and its segments together")]
    public void AGenerationPublishesEverythingItDerivedFrom()
    {
        using var session = new TemporarySession();

        DerivedGenerationResult result = Publish(session.Store, [Row(100, bytes: 1), Row(200, bytes: 2)]);

        Assert.Equal(1, result.Manifest.Generation);
        Assert.Equal("journal-0000000001.icatj", result.JournalName);
        Assert.Equal(2, result.JournalRecords);
        Assert.Equal(2, result.RowCount);
        PublishedSegmentSummary segment = Assert.Single(result.Segments);
        Assert.Equal("seg-0000000001-0000.icats", segment.Name);
        Assert.Equal(["dict-0000000001-0001.icatd"], segment.DictionaryNames);

        // The committed boundary is what makes the generation name its evidence rather than imply a whole file.
        Assert.True(result.Manifest.Boundary.IsDeclared);
        Assert.Equal(result.JournalName, result.Manifest.Boundary.JournalName);
        Assert.Equal(2, result.Manifest.Boundary.CommittedRecords);
        StoreDependency journal = Assert.Single(
            result.Manifest.Dependencies,
            dependency => dependency.Kind == StoreDependencyKind.Journal);
        Assert.Equal(journal.LengthBytes, result.Manifest.Boundary.CommittedBytes);
        Assert.Equal(journal.Digest, result.Manifest.Boundary.Digest);

        SessionStore reopened = session.Reopen();
        Assert.Equal(1, reopened.Current!.Generation);
        Assert.Empty(reopened.Recovery.OrphanFiles);
        Assert.Equal([segment.Name], SessionSegments.Names(reopened.Current));
    }

    [Fact(DisplayName = "I15: a generation over its row bound publishes several segments, each complete")]
    public void AGenerationOverItsBoundSplitsIntoSegments()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = [.. Enumerable.Range(0, 7)
            .Select(index => Row(100 + index, bytes: index, ordinal: (ulong)index + 1))];

        DerivedGenerationResult result = Publish(
            session.Store,
            rows,
            options: new() { RowsPerSegment = 3 });

        Assert.Equal(3, result.Segments.Count);
        Assert.Equal([3, 3, 1], result.Segments.Select(segment => segment.RowCount));
        Assert.Equal(7, result.RowCount);
        Assert.Equal(
            ["seg-0000000001-0000.icats", "seg-0000000001-0001.icats", "seg-0000000001-0002.icats"],
            result.Segments.Select(segment => segment.Name));

        // Each segment publishes its own dictionaries, so its codes mean what they mean without the others.
        Assert.Equal(
            ["dict-0000000001-0001.icatd", "dict-0000000001-0002.icatd", "dict-0000000001-0003.icatd"],
            result.Segments.SelectMany(segment => segment.DictionaryNames));
        Assert.Equal(
            rows.Select(row => row.NativeTicks),
            result.Segments.SelectMany(segment => AllTicks(session.Store, result.Manifest, segment.Name)));
    }

    [Fact(DisplayName = "I15: an abandoned generation publishes nothing and leaves only staging files")]
    public void AnAbandonedGenerationPublishesNothing()
    {
        using var session = new TemporarySession();

        using (DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(
            session.Store,
            Identity,
            TestClock,
            Committed))
        {
            builder.AddRow(Row(100, bytes: 1));
            builder.FlushSegment();
        }

        SessionStore reopened = session.Reopen();
        Assert.Null(reopened.Current);
        Assert.NotEmpty(reopened.Recovery.RemovedStagingFiles);
        Assert.Empty(reopened.Recovery.OrphanFiles);
    }

    [Fact(DisplayName = "I15: a segment referencing a dictionary the generation does not name is refused")]
    public void ASegmentWithAMissingDictionaryIsRefused()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult result = Publish(session.Store, [Row(100, bytes: 1)]);
        File.Delete(Path.Combine(session.Path, result.Segments[0].DictionaryNames[0]));

        Assert.ThrowsAny<IOException>(() =>
            SessionSegments.Open(session.Store.Root, result.Manifest, result.Segments[0].Name));
    }

    [Fact(DisplayName = "I8: a generation whose rows name another clock than its journal is refused")]
    public void AGenerationWithTwoClocksIsRefused()
    {
        using var session = new TemporarySession();
        SourceClockDescriptor other = new(
            new(Guid.Parse("99999999-8888-4777-8666-555544443333")),
            HostId.Derive("segment-v1-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 60);

        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            DerivedGenerationBuilder.Begin(session.Store, Identity, other, Committed));

        Assert.Contains("name one clock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I3: the rows inside an interval are found by search, and both ends are half-open")]
    public void RowsWithinAnIntervalAreHalfOpen()
    {
        SegmentReaderV1 segment = Build(
            [.. new long[] { 100, 200, 200, 300, 400 }.Select((ticks, index) => Row(ticks, bytes: 1, ordinal: (ulong)index + 1))]);

        // [200, 400) holds both readings at 200 and the one at 300, and not the one at its exclusive end.
        Assert.Equal((1, 4), segment.RowsWithin(new TimeRange(200, 400)));
        Assert.Equal((0, 1), segment.RowsWithin(new TimeRange(0, 101)));
        Assert.Equal((4, 5), segment.RowsWithin(new TimeRange(400, 401)));

        // An interval that misses the segment is the empty range, before, after or between two readings.
        Assert.Equal((0, 0), segment.RowsWithin(new TimeRange(0, 100)));
        Assert.Equal((0, 0), segment.RowsWithin(new TimeRange(401, 1_000)));
        (int first, int end) = segment.RowsWithin(new TimeRange(201, 300));
        Assert.Equal(first, end);
    }

    [Fact(DisplayName = "I6: a domain measurement keeps every side apart and counts what it left out, by reason")]
    public void ADomainMeasurementKeepsSidesApart()
    {
        SegmentReaderV1 segment = Build(
            [
                Row(100, bytes: 100, side: AccountingSide.SendSide),
                Row(200, bytes: 60, side: AccountingSide.ReceiveSide),
                Row(300, bytes: 0, side: AccountingSide.EndpointActivity),
                Row(400, bytes: null, availability: FieldAvailability.EventLost, side: AccountingSide.ReceiveSide),
                Row(500, bytes: 4_096, domain: ByteDomain.RequestedIo),
                Row(600, bytes: 1_024, domain: ByteDomain.CompletedIo),
                Row(700, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false),
                Row(800, bytes: 7, layer: ObservationLayer.Application, mechanism: Mechanism.Rpc),
                Row(900, bytes: 9, side: AccountingSide.SendSide),
            ]);

        DomainMeasurement measured = SegmentMeasurement.MeasureDomain(
            segment,
            new()
            {
                Domain = ByteDomain.TransportObserved,
                Layer = ObservationLayer.Transport,
                Interval = new TimeRange(100, 900),
                EvidenceSides = [AccountingSide.ReceiveSide],
                EvidenceLimit = 5,
            });

        Assert.Equal(
            [AccountingSide.SendSide, AccountingSide.ReceiveSide, AccountingSide.EndpointActivity],
            measured.Sides.Select(side => side.Side));
        Assert.Equal(100, measured.Side(AccountingSide.SendSide)!.TotalBytes);
        SideMeasurement received = measured.Side(AccountingSide.ReceiveSide)!;
        Assert.Equal((60, 1, 1), (received.TotalBytes, received.KnownContributions, received.UnknownContributions));
        Assert.Equal(1, received.UnknownReasons[FieldAvailability.EventLost]);

        // An observed zero is a known contribution, not an absence.
        Assert.Equal((0, 1), (measured.Side(AccountingSide.EndpointActivity)!.TotalBytes, measured.Side(AccountingSide.EndpointActivity)!.KnownContributions));

        // What was left out is counted on its own ground, and the other domains are named rather than summed.
        Assert.Equal(2, measured.ExcludedOtherDomain);
        Assert.Equal(1, measured.OtherDomains[ByteDomain.RequestedIo]);
        Assert.Equal(1, measured.OtherDomains[ByteDomain.CompletedIo]);
        Assert.Equal(1, measured.ExcludedNoDeclaredSlot);
        Assert.Equal(1, measured.ExcludedByProjection);
        Assert.Equal(1, measured.ExcludedOutsideInterval);

        // Evidence is the rows of the side asked for, in segment order.
        Assert.Equal([200L, 400L], measured.EvidenceRows.Select(row => segment.Row(row).NativeTicks));

        // The one-side sum is the same measurement with one side taken and the rest counted as another side.
        ByteSumResult sent = SegmentMeasurement.SumBytes(
            segment,
            new()
            {
                Domain = ByteDomain.TransportObserved,
                Side = AccountingSide.SendSide,
                Layer = ObservationLayer.Transport,
                Interval = new TimeRange(100, 900),
            });
        Assert.Equal((100, 1, 3), (sent.TotalBytes, sent.KnownContributions, sent.ExcludedOtherSide));
        Assert.Equal(1, sent.ExcludedOutsideInterval);
    }

    [Fact(DisplayName = "I8: a journal's clock is read without decoding a batch, and a torn one is refused")]
    public void AJournalClockIsReadWithoutItsBatches()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult result = Publish(session.Store, [Row(100, bytes: 1), Row(200, bytes: 2)]);

        SourceClockDescriptor? clock = SessionSegments.SourceClock(session.Store.Root, result.Manifest);
        Assert.Equal(TestClock, clock);

        byte[] journal = File.ReadAllBytes(Path.Combine(session.Path, result.JournalName));
        using (var whole = new MemoryStream(journal))
        {
            Assert.Equal(Capture, JournalV1Reader.ReadSourceClock(whole).CaptureId);
        }

        using var torn = new MemoryStream(journal, 0, JournalV1Codec.HeaderLength + 12);
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => JournalV1Reader.ReadSourceClock(torn));
        Assert.Contains("ends inside", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I18: a lease holds the manifest it acquired, and a later commit does not move it")]
    public void ALeaseHoldsTheManifestItAcquired()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult first = Publish(session.Store, [Row(100, bytes: 1)]);
        using EvidenceLease lease = session.Store.AcquireLease();

        DerivedGenerationResult second = Publish(session.Store, [Row(300, bytes: 3)]);

        Assert.Equal(2, session.Store.Current!.Generation);
        Assert.Same(first.Manifest, lease.Manifest);
        Assert.Equal(1, lease.Generation);
        Assert.DoesNotContain(second.Segments[0].Name, SessionSegments.Names(lease.Manifest));
    }

    private static readonly DateTimeOffset Committed = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static SegmentIdentityV1 Identity { get; } = new()
    {
        CaptureId = Capture,
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        Derivation = NormalizerContractVersion.V1,
    };

    private static SourceClockDescriptor TestClock { get; } = new(
        Clock,
        HostId.Derive("segment-v1-tests"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        0,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    private static ObservationRowV1 Row(
        long ticks,
        long? bytes,
        FieldAvailability availability = FieldAvailability.Present,
        bool declareSlot = true,
        ByteDomain domain = ByteDomain.TransportObserved,
        AccountingSide side = AccountingSide.SendSide,
        ObservationLayer layer = ObservationLayer.Transport,
        Mechanism mechanism = Mechanism.Tcp,
        ulong? ordinal = null) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal ?? (ulong)ticks,
        FactKey = FactKey.Create("network-transfer"),
        ProviderId = Provider,
        EventId = 10,
        DescriptorVersion = 0,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = 10,
        NativeTicks = ticks,
        HeaderProcessId = 1_234,
        HeaderThreadId = 5_678,
        ProcessorNumber = 3,
        Mechanism = mechanism,
        Layer = layer,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        ByteValue = bytes,
        ByteDomain = declareSlot ? domain : null,
        AccountingSide = declareSlot ? side : null,
        MeasurementUnit = declareSlot ? MeasurementUnit.Bytes : null,
        ByteAvailability = availability,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = bytes is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,
        TimingQuality = QualityLevel.Qualified,
    };

    private static byte[] Encode(IEnumerable<ObservationRowV1> rows) => EncodeWithDictionaries(rows).Bytes;

    private static (byte[] Bytes, IReadOnlyList<SegmentDictionaryV1> Dictionaries) EncodeWithDictionaries(
        IEnumerable<ObservationRowV1> rows)
    {
        var writer = new SegmentWriterV1(Identity, 0);
        foreach (ObservationRowV1 row in rows)
        {
            writer.Add(row);
        }

        SegmentBuildResult built = writer.Build();
        return (built.Segment, built.Dictionaries);
    }

    private static SegmentReaderV1 Build(IEnumerable<ObservationRowV1> rows)
    {
        (byte[] bytes, IReadOnlyList<SegmentDictionaryV1> dictionaries) = EncodeWithDictionaries(rows);
        return SegmentReaderV1.Open(bytes, dictionaries);
    }

    /// <summary>
    /// Publishes a generation the way a real derivation does: one admitted journal record per derived row, so
    /// the committed boundary the manifest carries describes evidence that is actually in the file.
    /// </summary>
    private static DerivedGenerationResult Publish(
        SessionStore store,
        IEnumerable<ObservationRowV1> rows,
        DerivedGenerationOptions? options = null)
    {
        using DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(
            store,
            Identity,
            TestClock,
            Committed,
            options);
        var schemas = new JournalV1SchemaTable();
        uint schema = schemas.Intern(Provider, 10, 0, "sha256:" + new string('a', 64));
        uint policy = schemas.InternPolicy("metadata-only-admitted-projection-v1");
        builder.Journal.WriteSchemas(schemas);
        ulong index = 0;
        foreach (ObservationRowV1 row in rows)
        {
            builder.AddRow(row with { JournalRecordIndex = index++ });
            builder.Journal.Append(Envelope(row, schema, policy));
        }

        return builder.Complete(Committed);
    }

    private static RecordEnvelopeV1 Envelope(ObservationRowV1 row, uint schema, uint policy) => new()
    {
        CaptureId = Capture,
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
            row.ActivityId ?? Guid.Empty,
            row.RelatedActivityId ?? Guid.Empty),
        BufferContext = new(row.ProcessorNumber, 0),
        ClockId = Clock,
        TimestampEncoding = TimestampEncoding.Qpc,
        NativeTicks = row.NativeTicks,
        PointerSize = 8,
        SchemaReference = schema,
        AdmissionPolicyReference = policy,
        ExtendedItems = [],
        OmittedExtendedItemCount = 0,
        Body = BodyV1.None,
    };

    private static ObservationRowV1 Read(SessionStore store, SessionManifestV1 manifest, string segment, int row) =>
        SessionSegments.Open(store.Root, manifest, segment).Row(row);

    private static IEnumerable<long> AllTicks(SessionStore store, SessionManifestV1 manifest, string segment)
    {
        SegmentReaderV1 reader = SessionSegments.Open(store.Root, manifest, segment);
        for (int row = 0; row < reader.RowCount; row++)
        {
            yield return reader.Row(row).NativeTicks;
        }
    }

    private static ByteSumResult Sum(
        SessionStore store,
        SessionManifestV1 manifest,
        string segment,
        ObservationLayer? layer = null) =>
        SegmentMeasurement.SumBytes(
            SessionSegments.Open(store.Root, manifest, segment),
            new()
            {
                Domain = ByteDomain.TransportObserved,
                Side = AccountingSide.SendSide,
                Layer = layer,
            });

    private sealed class TemporarySession : IDisposable
    {
        private static readonly Guid Session = Guid.Parse("7f1e2d3c-4b5a-4968-8778-99aabbccddee");

        public TemporarySession()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterCat.Storage.Tests.Segments",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "segment-v1-tests");
        }

        public string Path { get; }

        public SessionStore Store { get; private set; }

        public SessionStore Reopen()
        {
            Store = SessionStore.Open(LocalOwnedDirectory.Open(Path), Session, "segment-v1-tests");
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
