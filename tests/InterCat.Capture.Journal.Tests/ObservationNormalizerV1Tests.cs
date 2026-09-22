using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// The first derivation: one admitted journal record into one `observation-v1` row. The tests are about what
/// the normalizer refuses to invent as much as what it carries over — a derived row is where a fabricated
/// value would stop looking fabricated.
/// </summary>
public sealed class ObservationNormalizerV1Tests
{
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

    [Fact(DisplayName = "R2: a derived row carries the source's own attribution, measurement and labels")]
    public void ADerivedRowCarriesTheSourcesOwnFacts()
    {
        AdmittedEventPlan plan = TransferPlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        AdmittedEvent admitted = Admitted();
        admitted.SetSlot(0, 4_242);
        admitted.SetSlot(1, 1_460);
        admitted.SetSlot(2, 0x0100007F);
        admitted.SetSlot(3, 0x44CB);
        admitted.SetSlot(4, 0x0100007F);
        admitted.SetSlot(5, 0xBB01);
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());

        ObservationRowV1 row = normalizer.ToRow(envelope, plan, journalRecordIndex: 11);

        Assert.Equal(Mechanism.Tcp, row.Mechanism);
        Assert.Equal(ObservationLayer.Transport, row.Layer);
        Assert.Equal(ObservationKind.Send, row.Kind);
        Assert.Equal(Direction.Outbound, row.Direction);
        Assert.Equal(4_242, row.OwnerProcessId);
        Assert.Equal(11u, row.JournalRecordIndex);
        Assert.Equal(1_460, row.ByteValue);
        Assert.Equal(ByteDomain.TransportObserved, row.ByteDomain);
        Assert.Equal(AccountingSide.SendSide, row.AccountingSide);
        Assert.Equal(MeasurementUnit.Bytes, row.MeasurementUnit);
        Assert.Equal(FieldAvailability.Present, row.ByteAvailability);
        Assert.Equal(QualityLevel.Proven, row.AttributionQuality);
        Assert.Equal(QualityLevel.Proven, row.MeasurementQuality);

        // The documented transform is applied to the derived endpoint, and the header's process stays context.
        Assert.Equal<byte?>(4, row.EndpointAddressFamily);
        Assert.Equal(0x7F000001u, row.SourceEndpointAddress);
        Assert.Equal<ushort?>(52_036, row.SourceEndpointPort);
        Assert.Equal<ushort?>(443, row.DestinationEndpointPort);
        Assert.Equal(42, row.HeaderProcessId);
        Assert.NotEqual(row.HeaderProcessId, row.OwnerProcessId);

        // The reading is the source's own, and the derived session-relative instant sits beside it.
        Assert.Equal(envelope.NativeTicks, row.NativeTicks);
        Assert.Equal(12_345_600, row.SessionRelativeTicks);
        Assert.Equal(QualityLevel.Proven, row.TimingQuality);
        Assert.Null(row.Validate());
    }

    [Fact(DisplayName = "R3: a descriptor with no byte field reports it inapplicable, not zero")]
    public void ADescriptorWithNoByteFieldReportsItInapplicable()
    {
        AdmittedEventPlan plan = LifecyclePlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        AdmittedEvent admitted = Admitted(plan);
        admitted.SetSlot(0, 1_000);
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());

        ObservationRowV1 row = normalizer.ToRow(envelope, plan);

        Assert.Null(row.ByteValue);
        Assert.Null(row.ByteDomain);
        Assert.Null(row.AccountingSide);
        Assert.Null(row.MeasurementUnit);
        Assert.Equal(FieldAvailability.NotApplicable, row.ByteAvailability);
        Assert.Equal(ObservationLayer.Lifecycle, row.Layer);
        Assert.Null(row.Validate());
    }

    [Fact(DisplayName = "R3: a byte field the source declares but does not expose stays an unknown in its slot")]
    public void AnUnexposedByteFieldStaysAnUnknownInItsSlot()
    {
        // The capability report says the field exists and why it was not admitted. That is an unknown value in
        // a declared slot, which belongs in the denominator, not an absent slot that belongs nowhere.
        AdmittedEventPlan plan = TransferPlan() with
        {
            Slots = [new("PID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None)],
            FieldReport =
            [
                new("PID", FieldAvailability.Present, FieldRole.ProcessAttribution),
                new(
                    "size",
                    FieldAvailability.SchemaUnknown,
                    FieldRole.ByteCount,
                    Unit: MeasurementUnit.Bytes,
                    ByteDomain: ByteDomain.TransportObserved),
            ],
            MinimumBodyLength = 4,
        };
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        AdmittedEvent admitted = Admitted();
        admitted.SetSlot(0, 4_242);
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());

        ObservationRowV1 row = normalizer.ToRow(envelope, plan);

        Assert.Null(row.ByteValue);
        Assert.Equal(ByteDomain.TransportObserved, row.ByteDomain);
        Assert.Equal(MeasurementUnit.Bytes, row.MeasurementUnit);
        Assert.Equal(FieldAvailability.SchemaUnknown, row.ByteAvailability);
        Assert.Equal(QualityLevel.UnknownQuality, row.MeasurementQuality);
        Assert.Null(row.Validate());
    }

    [Fact(DisplayName = "I2: the same record and derivation yield the same identity every time")]
    public void TheSameRecordYieldsTheSameIdentity()
    {
        AdmittedEventPlan plan = TransferPlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        CaptureId capture = CaptureId.New();
        AdmittedEvent admitted = Admitted();
        admitted.SetSlot(1, 64);
        using RecordEnvelopeV1 first = mapper.ToEnvelope(admitted, plan, capture);
        using RecordEnvelopeV1 second = mapper.ToEnvelope(admitted, plan, capture);

        ObservationRowV1 left = normalizer.ToRow(first, plan);
        ObservationRowV1 right = normalizer.ToRow(second, plan);

        Assert.Equal(left, right);
        Assert.Equal(
            left.ObservationIdIn(capture, ObservationNormalizerV1.ContractVersion),
            right.ObservationIdIn(capture, ObservationNormalizerV1.ContractVersion));
        Assert.Equal(FactKey.Create("network-transfer"), left.FactKey);
        Assert.Equal(NormalizerContractVersion.V1, ObservationNormalizerV1.ContractVersion);
    }

    [Fact(DisplayName = "I8: a record naming another clock is refused rather than converted")]
    public void ARecordFromAnotherClockIsRefused()
    {
        AdmittedEventPlan plan = TransferPlan();
        (ObservationNormalizerV1 normalizer, _) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], ClockId.New());
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(Admitted(), plan, CaptureId.New());

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => normalizer.ToRow(envelope, plan));

        Assert.Contains("never converted against a clock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I12: an absent activity id stays absent rather than becoming an empty identifier")]
    public void AnAbsentActivityIdStaysAbsent()
    {
        AdmittedEventPlan plan = TransferPlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        AdmittedEvent admitted = Admitted();
        admitted.ActivityId = Guid.Empty;
        admitted.RelatedActivityId = Guid.Empty;
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());

        ObservationRowV1 row = normalizer.ToRow(envelope, plan);

        Assert.Null(row.ActivityId);
        Assert.Null(row.RelatedActivityId);
    }

    [Fact(DisplayName = "I13: a row records what the capture did with the record's extended data")]
    public void ARowRecordsWhatHappenedToExtendedData()
    {
        AdmittedEventPlan plan = TransferPlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(plan)], clock.Id);
        AdmittedEvent requested = Admitted();
        requested.BeginExtendedData(ExtendedDataAvailability.Captured, 2);
        Assert.True(requested.TryAppendExtendedItem(
            EtwExtendedDataTypes.ProcessStartKey,
            linkage: 0,
            [1, 2, 3, 4, 5, 6, 7, 8],
            originalLength: 8));
        requested.RecordOmittedExtendedItem();
        using RecordEnvelopeV1 withItems = mapper.ToEnvelope(requested, plan, CaptureId.New());

        ObservationRowV1 row = normalizer.ToRow(withItems, plan);

        Assert.True(row.Markers.HasFlag(SegmentRowMarkers.ExtendedDataRequested));
        Assert.True(row.Markers.HasFlag(SegmentRowMarkers.ExtendedItemsOmitted));

        // A run that never asked for extended data reports nothing, so a zero count describes the capture's
        // configuration rather than the record.
        using RecordEnvelopeV1 without = mapper.ToEnvelope(Admitted(), plan, CaptureId.New());
        Assert.False(normalizer.ToRow(without, plan).Markers.HasFlag(SegmentRowMarkers.ExtendedDataRequested));
    }

    [Fact(DisplayName = "I15: every derived row is writable into a segment as it stands")]
    public void EveryDerivedRowIsWritable()
    {
        AdmittedEventPlan transfer = TransferPlan();
        AdmittedEventPlan lifecycle = LifecyclePlan();
        (ObservationNormalizerV1 normalizer, SourceClockDescriptor clock) = Normalizer();
        var mapper = new AdmittedEventEnvelopeMapper([Source(transfer), Source(lifecycle)], clock.Id);
        CaptureId capture = CaptureId.New();
        var writer = new SegmentWriterV1(
            new()
            {
                CaptureId = capture,
                ClockId = clock.Id,
                TimestampEncoding = clock.Encoding,
                Derivation = ObservationNormalizerV1.ContractVersion,
            },
            0);

        for (int index = 0; index < 4; index++)
        {
            AdmittedEventPlan plan = index % 2 == 0 ? transfer : lifecycle;
            AdmittedEvent admitted = Admitted(plan);
            admitted.RecordOrdinal = index;
            admitted.TimestampQpc = 1_000 + index;
            admitted.SetSlot(0, 4_242);
            if (plan.Slots.Any(slot => slot.Role == FieldRole.ByteCount))
            {
                admitted.SetSlot(1, 128 * (index + 1));
            }

            using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, capture);
            writer.Add(normalizer.ToRow(envelope, plan, (ulong)index));
        }

        SegmentBuildResult built = writer.Build();
        SegmentReaderV1 segment = SegmentReaderV1.Open(built.Segment, built.Dictionaries);

        Assert.Equal(4, segment.RowCount);
        Assert.Equal(2, segment.Column(SegmentColumnId.ByteValue)!.KnownCount);
        Assert.Equal(2, segment.Column(SegmentColumnId.ByteValue)!.UnknownCount);
        Assert.Equal(
            [ObservationLayer.Transport, ObservationLayer.Lifecycle],
            Enumerable.Range(0, 4).Select(row => segment.Row(row).Layer).Distinct().Order());

        ByteSumResult sent = SegmentMeasurement.SumBytes(
            segment,
            new()
            {
                Domain = ByteDomain.TransportObserved,
                Side = AccountingSide.SendSide,
                Layer = ObservationLayer.Transport,
            });
        Assert.Equal(128 + 384, sent.TotalBytes);
        Assert.Equal(2, sent.ExcludedByProjection);
    }

    private static (ObservationNormalizerV1 Normalizer, SourceClockDescriptor Clock) Normalizer()
    {
        SourceClockDescriptor clock = new(
            ClockId.Derive("observation-normalizer-tests"),
            HostId.Derive("observation-normalizer-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        return (new(clock), clock);
    }

    private static AdmittedEvent Admitted(AdmittedEventPlan? plan = null) => new()
    {
        SourceIndex = plan?.SourceIndex ?? 0,
        EventId = plan?.EventId ?? 10,
        Version = plan?.Version ?? 0,
        Opcode = 10,
        TimestampQpc = 123_456,
        TimestampUtcTicks = 638_000_000_000_000_000,
        HeaderProcessId = 42,
        HeaderThreadId = 43,
        ProcessorNumber = 7,
        RecordOrdinal = 99,
        ActivityId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        RelatedActivityId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
    };

    private static AdmittedEventPlan TransferPlan() => new()
    {
        SourceIndex = 0,
        ProviderGuid = Provider,
        EventId = 10,
        Version = 0,
        Name = "TCPv4 data sent",
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        MinimumBodyLength = 20,
        PointerSize = 8,
        SchemaFingerprint = "sha256:fixture-transfer",
        BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
        Slots =
        [
            new("PID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None),
            new("size", FieldRole.ByteCount, 4, 4, MeasurementUnit.Bytes, ByteDomain.TransportObserved, SlotTransform.None),
            new("saddr", FieldRole.SourceEndpoint, 8, 4, null, null, SlotTransform.NetworkOrderIpv4Address),
            new("sport", FieldRole.SourceEndpoint, 12, 2, null, null, SlotTransform.NetworkOrderPort),
            new("daddr", FieldRole.DestinationEndpoint, 14, 4, null, null, SlotTransform.NetworkOrderIpv4Address),
            new("dport", FieldRole.DestinationEndpoint, 18, 2, null, null, SlotTransform.NetworkOrderPort),
        ],
        FieldReport =
        [
            new("PID", FieldAvailability.Present, FieldRole.ProcessAttribution),
            new(
                "size",
                FieldAvailability.Present,
                FieldRole.ByteCount,
                Unit: MeasurementUnit.Bytes,
                ByteDomain: ByteDomain.TransportObserved),
        ],
    };

    private static AdmittedEventPlan LifecyclePlan() => new()
    {
        SourceIndex = 1,
        ProviderGuid = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716"),
        EventId = 1,
        Version = 4,
        Name = "Process start",
        Mechanism = Mechanism.ProcessLifecycle,
        Layer = ObservationLayer.Lifecycle,
        Kind = ObservationKind.Create,
        Direction = Direction.DirectionNotApplicable,
        MinimumBodyLength = 4,
        PointerSize = 8,
        SchemaFingerprint = "sha256:fixture-lifecycle",
        BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
        Slots = [new("ProcessID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None)],
        FieldReport = [new("ProcessID", FieldAvailability.Present, FieldRole.ProcessAttribution)],
    };

    private static SourceAdmissionPlan Source(AdmittedEventPlan plan) => new()
    {
        SourceId = $"fixture/source/{plan.SourceIndex}",
        ProviderGuid = plan.ProviderGuid,
        SourceIndex = plan.SourceIndex,
        Events = [plan],
        Diagnostics = [],
    };
}
