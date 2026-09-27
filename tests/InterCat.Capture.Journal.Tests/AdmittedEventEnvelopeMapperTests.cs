using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

public sealed class AdmittedEventEnvelopeMapperTests
{
    [Fact(DisplayName = "IC-011: every field in the callback envelope survives journal-v1 mapping")]
    public void CallbackEnvelopeRoundTripsExactly()
    {
        AdmittedEventPlan plan = BuildEventPlan();
        SourceAdmissionPlan source = BuildSource(plan);
        var mapper = new AdmittedEventEnvelopeMapper([source], ClockId.New());
        AdmittedEvent admitted = BuildAdmitted();
        admitted.SetSlot(0, 123);
        admitted.SetSlot(3, -456);
        admitted.SetIdentifier(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        admitted.SetName("named resource", truncated: true);
        admitted.BeginExtendedData(ExtendedDataAvailability.Captured, 3);
        Assert.True(admitted.TryAppendExtendedItem(
            EtwExtendedDataTypes.ProcessStartKey,
            linkage: 0,
            [1, 2, 3, 4, 5, 6, 7, 8],
            originalLength: 8));
        Assert.True(admitted.TryAppendExtendedItem(
            EtwExtendedDataTypes.EventKey,
            linkage: 1,
            new byte[AdmittedEvent.MaximumExtendedItemBytes],
            originalLength: 900));
        admitted.RecordOmittedExtendedItem();
        CaptureId captureId = CaptureId.New();

        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, captureId);
        AdmittedEvent replayed = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan);

        Assert.Equal(captureId, envelope.CaptureId);
        Assert.Equal(BodyDispositionV1.Retained, envelope.Body.Disposition);
        Assert.Equal(BodyClassificationV1.ApprovedMetadata, envelope.Body.Classification);
        Assert.Equal(envelope.Body.RetainedLength, envelope.Body.OriginalLength);
        Assert.Equal(Assert.Single(mapper.Schemas.Schemas).Reference, envelope.SchemaReference);
        Assert.Equal(
            CaptureBodyAdmissionPolicies.MetadataOnlyPolicyId,
            mapper.Schemas.FindPolicy(envelope.AdmissionPolicyReference)!.PolicyId);
        Assert.Equal(admitted.SourceIndex, replayed.SourceIndex);
        Assert.Equal(admitted.EventId, replayed.EventId);
        Assert.Equal(admitted.Version, replayed.Version);
        Assert.Equal(admitted.Opcode, replayed.Opcode);
        Assert.Equal(admitted.TimestampQpc, replayed.TimestampQpc);
        Assert.Equal(admitted.TimestampUtcTicks, replayed.TimestampUtcTicks);
        Assert.Equal(admitted.HeaderProcessId, replayed.HeaderProcessId);
        Assert.Equal(admitted.HeaderThreadId, replayed.HeaderThreadId);
        Assert.Equal(admitted.ProcessorNumber, replayed.ProcessorNumber);
        Assert.Equal(admitted.RecordOrdinal, replayed.RecordOrdinal);
        Assert.Equal(admitted.ActivityId, replayed.ActivityId);
        Assert.Equal(admitted.RelatedActivityId, replayed.RelatedActivityId);
        Assert.Equal(admitted.KnownSlotMask, replayed.KnownSlotMask);
        Assert.True(replayed.TryGetSlot(0, out long first));
        Assert.Equal(123, first);
        Assert.True(replayed.TryGetSlot(3, out long fourth));
        Assert.Equal(-456, fourth);
        Assert.Equal(admitted.Identifier, replayed.Identifier);
        Assert.True(replayed.NameTruncated);
        Span<char> name = stackalloc char[AdmittedEvent.MaximumNameLength];
        int length = replayed.CopyName(name);
        Assert.Equal("named resource", new string(name[..length]));

        Assert.Equal(ExtendedDataAvailability.Captured, replayed.ExtendedData);
        Assert.Equal(3, replayed.ExtendedItemsPresent);
        Assert.Equal(1, replayed.ExtendedItemsOmitted);
        Assert.Equal(2, replayed.ExtendedItemsCopied);
        AdmittedExtendedItem startKey = replayed.GetExtendedItem(0);
        Assert.Equal(EtwExtendedDataTypes.ProcessStartKey, startKey.Type);
        Assert.Equal(8, startKey.OriginalLength);
        Assert.False(startKey.Truncated);
        Span<byte> bytes = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        Assert.Equal(8, replayed.CopyExtendedItemBytes(0, bytes));
        Assert.Equal<byte>([1, 2, 3, 4, 5, 6, 7, 8], bytes[..8].ToArray());

        AdmittedExtendedItem eventKey = replayed.GetExtendedItem(1);
        Assert.Equal(900, eventKey.OriginalLength);
        Assert.True(eventKey.Truncated);
        Assert.Equal(AdmittedEvent.MaximumExtendedItemBytes, eventKey.CopiedLength);
    }

    [Fact(DisplayName = "IC-011: a record's IPv6 addresses survive journal-v1 mapping, and a record without one keeps its bytes")]
    public void AddressesRoundTripAndAnAddresslessRecordKeepsItsBytes()
    {
        AdmittedEventPlan plan = BuildEventPlan();
        var mapper = new AdmittedEventEnvelopeMapper([BuildSource(plan)], ClockId.New());
        AdmittedEvent plain = BuildAdmitted();
        plain.SetSlot(0, 123);
        plain.SetIdentifier(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        AdmittedEvent addressed = plain;
        UInt128 documentation = ((UInt128)0x2001_0DB8 << 96) | 0x05EC;
        addressed.SetAddress(0, UInt128.One);
        addressed.SetAddress(1, documentation);
        AdmittedEvent destinationOnly = plain;
        destinationOnly.SetAddress(1, documentation);
        CaptureId capture = CaptureId.New();

        using RecordEnvelopeV1 withoutAddress = mapper.ToEnvelope(plain, plan, capture);
        using RecordEnvelopeV1 withAddresses = mapper.ToEnvelope(addressed, plan, capture);
        using RecordEnvelopeV1 withOne = mapper.ToEnvelope(destinationOnly, plan, capture);

        // A record with no address is written as every earlier build wrote it; one with addresses carries a mask and each
        // address's 16 bytes, in network order, right after its identifier.
        byte[] plainBytes = withoutAddress.Body.Bytes.ToArray();
        byte[] addressedBytes = withAddresses.Body.Bytes.ToArray();
        Assert.Equal("IAP1"u8.ToArray(), plainBytes[..4]);
        Assert.Equal("IAP2"u8.ToArray(), addressedBytes[..4]);
        int afterIdentifier = 4 + 8 + 1 + (8 * AdmissionPlanCompiler.MaximumSlots) + 1 + 16;
        Assert.Equal(plainBytes[4..afterIdentifier], addressedBytes[4..afterIdentifier]);
        Assert.Equal(3, addressedBytes[afterIdentifier]);
        Assert.Equal([.. new byte[15], 1], addressedBytes[(afterIdentifier + 1)..(afterIdentifier + 17)]);
        Assert.Equal(new byte[] { 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 0xEC },
            addressedBytes[(afterIdentifier + 17)..(afterIdentifier + 33)]);
        Assert.Equal(plainBytes[afterIdentifier..], addressedBytes[(afterIdentifier + 33)..]);

        Assert.Equal(0, AdmittedEventEnvelopeMapper.FromEnvelope(withoutAddress, plan).KnownAddressMask);
        AdmittedEvent replayed = AdmittedEventEnvelopeMapper.FromEnvelope(withAddresses, plan);
        Assert.True(replayed.TryGetAddress(0, out UInt128 source));
        Assert.True(replayed.TryGetAddress(1, out UInt128 destination));
        Assert.Equal((UInt128.One, documentation), (source, destination));
        Assert.Equal(plain.Identifier, replayed.Identifier);
        AdmittedEvent one = AdmittedEventEnvelopeMapper.FromEnvelope(withOne, plan);
        Assert.False(one.TryGetAddress(0, out _));
        Assert.True(one.TryGetAddress(1, out UInt128 only) && only == documentation);

        // A mask naming no address, or one a record cannot hold, is refused rather than read.
        foreach (byte mask in new byte[] { 0, 4 })
        {
            byte[] forged = [.. addressedBytes];
            forged[afterIdentifier] = mask;
            RecordEnvelopeV1 refused = withAddresses with
            {
                Body = withAddresses.Body with { Bytes = EnvelopeBuffer.CopyOf(forged), OriginalLength = forged.Length },
            };
            Assert.Throws<InvalidDataException>(() => AdmittedEventEnvelopeMapper.FromEnvelope(refused, plan));
        }
    }

    [Fact(DisplayName = "R17: an extended item the policy denies keeps its type and loses its bytes")]
    public void DeniedExtendedTypeIsCountedNotPersisted()
    {
        AdmittedEventPlan plan = BuildEventPlan();
        var mapper = new AdmittedEventEnvelopeMapper([BuildSource(plan)], ClockId.New());
        AdmittedEvent admitted = BuildAdmitted();
        admitted.BeginExtendedData(ExtendedDataAvailability.Captured, 2);
        Assert.True(admitted.TryAppendExtendedItem(EtwExtendedDataTypes.Sid, 0, [9, 9, 9, 9], 4));
        Assert.True(admitted.TryAppendExtendedItem(EtwExtendedDataTypes.ContainerId, 0, [7, 7], 2));

        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());
        AdmittedEvent replayed = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan);

        Assert.Equal(1, envelope.OmittedExtendedItemCount);
        ExtendedItemV1 persisted = Assert.Single(envelope.ExtendedItems);
        Assert.Equal(EtwExtendedDataTypes.ContainerId, persisted.Type);
        Assert.DoesNotContain(
            envelope.ExtendedItems,
            item => item.Type == EtwExtendedDataTypes.Sid);
        Assert.Equal(1, replayed.ExtendedItemsCopied);
        Assert.Equal(1, replayed.ExtendedItemsOmitted);
        Assert.Equal(EtwExtendedDataTypes.ContainerId, replayed.GetExtendedItem(0).Type);
    }

    [Fact(DisplayName = "R21: a record whose extended items were unreachable replays as unavailable")]
    public void UnreachableExtendedDataStaysUnavailableAfterReplay()
    {
        AdmittedEventPlan plan = BuildEventPlan();
        var mapper = new AdmittedEventEnvelopeMapper([BuildSource(plan)], ClockId.New());
        AdmittedEvent admitted = BuildAdmitted();
        admitted.BeginExtendedData(ExtendedDataAvailability.UnavailableOnThisAdapter, 5);

        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());
        AdmittedEvent replayed = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan);

        Assert.Empty(envelope.ExtendedItems);
        Assert.Equal(ExtendedDataAvailability.UnavailableOnThisAdapter, replayed.ExtendedData);
        Assert.Equal(5, replayed.ExtendedItemsPresent);
        Assert.Equal(0, replayed.ExtendedItemsCopied);
    }

    [Fact(DisplayName = "IC-012: an envelope cannot be written or replayed against another descriptor plan")]
    public void EnvelopeDescriptorPlanMustMatch()
    {
        AdmittedEventPlan plan = BuildEventPlan();
        var mapper = new AdmittedEventEnvelopeMapper([BuildSource(plan)], ClockId.New());
        AdmittedEvent admitted = BuildAdmitted();
        AdmittedEventPlan wrong = plan with
        {
            EventId = plan.EventId + 1,
            SchemaFingerprint = "sha256:different-descriptor",
        };

        Assert.Throws<InvalidDataException>(() => mapper.ToEnvelope(admitted, wrong, CaptureId.New()));

        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(admitted, plan, CaptureId.New());
        Assert.Throws<InvalidDataException>(() => AdmittedEventEnvelopeMapper.FromEnvelope(envelope, wrong));
    }

    private static AdmittedEvent BuildAdmitted() => new()
    {
        SourceIndex = 4,
        EventId = 10,
        Version = 2,
        Opcode = 3,
        TimestampQpc = 123_456,
        TimestampUtcTicks = 638_000_000_000_000_000,
        HeaderProcessId = 42,
        HeaderThreadId = 43,
        ProcessorNumber = 7,
        RecordOrdinal = 99,
        ActivityId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        RelatedActivityId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
    };

    private static AdmittedEventPlan BuildEventPlan() => new()
    {
        SourceIndex = 4,
        ProviderGuid = Guid.Parse("44444444-4444-4444-8444-444444444444"),
        EventId = 10,
        Version = 2,
        Name = "fixture",
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        MinimumBodyLength = 8,
        PointerSize = 8,
        SchemaFingerprint = "sha256:fixture-callback-envelope",
        BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
        Slots = [],
        FieldReport = [],
    };

    private static SourceAdmissionPlan BuildSource(AdmittedEventPlan plan) => new()
    {
        SourceId = "fixture/source",
        ProviderGuid = plan.ProviderGuid,
        SourceIndex = plan.SourceIndex,
        Events = [plan],
        Diagnostics = [],
    };
}
