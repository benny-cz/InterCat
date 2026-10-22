using System.Globalization;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// An envelope's buffers come from one pool the whole process shares, and a test that writes or replays a batch holds
/// thousands of them at once. While it does, a size's bucket can be empty, and the next rent allocates an array that is
/// that test's doing, not the record's. What one record allocates is therefore measured with the pool to itself.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedEnvelopePool
{
    public const string Name = "Envelope pool";
}

/// <summary>
/// R11's admission and decode loops, as each record of a journal passes them: an admitted record is written as its
/// envelope, and an envelope is resolved against its retained plan, read back and derived. Each step allocates what it
/// hands on - the envelope, the row, the row's name, its field rows - and nothing besides.
/// </summary>
/// <remarks>
/// A step is measured warm on the calling thread, which runs it whole: the least of five runs of 64 calls, so one run
/// a collection or the runtime disturbs is not the step's own, and an object of the smallest size, 24 bytes, allocated
/// on one call in 64 still shows. What a step should allocate is measured the same way, as copies of what it returned.
/// </remarks>
[Collection(SharedEnvelopePool.Name)]
public sealed class DecodeAllocationTests
{
    [Fact(DisplayName = "R11: resolving and reading a record allocate nothing, and deriving it allocates only its rows")]
    public void DecodingAllocatesOnlyTheRowsItDerives()
    {
        var journal = new Journal();
        var fields = new List<SourceFieldRowV1>();
        var report = new List<string>();
        foreach ((string record, AdmittedEventPlan descriptor, AdmittedEvent admitted) in journal.Records())
        {
            using RecordEnvelopeV1 envelope = journal.Mapper.ToEnvelope(admitted, descriptor, journal.Capture);
            AdmittedEventPlan plan = journal.Plan.Resolve(envelope, journal.Mapper.Schemas);
            fields.Clear();
            ObservationRowV1 row = journal.Normalizer.ToRow(envelope, plan, 7, fields);
            SourceFieldRowV1[] derived = [.. fields];
            double rows = PerCall(() => _ = row with { })
                + (row.ResourceName is { } name ? PerCall(() => _ = new string(name.AsSpan())) : 0)
                + derived.Sum(field => PerCall(() => _ = field with { }));
            Check(report, record, "resolving it", 0, PerCall(() => journal.Plan.Resolve(envelope, journal.Mapper.Schemas)));
            Check(report, record, "reading it", 0, PerCall(() => AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan)));
            Check(report, record, "deriving it", rows, PerCall(() =>
            {
                fields.Clear();
                _ = journal.Normalizer.ToRow(envelope, plan, 7, fields);
            }));
        }

        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
    }

    [Fact(DisplayName = "R11: writing a record's envelope allocates only the envelope")]
    public void WritingAllocatesOnlyTheEnvelope()
    {
        var journal = new Journal();
        var report = new List<string>();
        foreach ((string record, AdmittedEventPlan descriptor, AdmittedEvent admitted) in journal.Records())
        {
            using RecordEnvelopeV1 sample = journal.Mapper.ToEnvelope(admitted, descriptor, journal.Capture);
            double envelope = PerCall(() => _ = sample with { })
                + PerCall(() => _ = sample.Body with { })
                + PerCall(() => EnvelopeBuffer.CopyOf(sample.Body.Bytes.ReadOnlySpan).Dispose());
            if (sample.ExtendedItems.Count > 0)
            {
                // Its list has room for every item the record copied, those the policy refused among them.
                envelope += PerCall(() => _ = new List<ExtendedItemV1>(admitted.ExtendedItemsCopied));
                foreach (ExtendedItemV1 item in sample.ExtendedItems)
                {
                    envelope += PerCall(() => _ = item with { }) + PerCall(() => EnvelopeBuffer.CopyOf(item.Bytes.ReadOnlySpan).Dispose());
                }
            }

            Check(report, record, "writing it", envelope, PerCall(() =>
                journal.Mapper.ToEnvelope(admitted, descriptor, journal.Capture).Dispose()));
        }

        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
    }

    [Fact(DisplayName = "R11: a replay reads its frames into one pooled buffer, so it allocates what its records are, not their bytes")]
    public void ReplayingAllocatesTheRecordsNotTheirBytes()
    {
        // Two journals of the same records in batches of 16, few enough that the shared pool keeps every body a batch
        // rents: one's records carry no name and the other's the longest, nearly three times the bytes. A frame read into
        // an array of its own would allocate those bytes again; read into the replay's pooled buffer, the two cost alike.
        var journal = new Journal();
        using MemoryStream plain = journal.Written(2_048, named: false);
        using MemoryStream named = journal.Written(2_048, named: true);
        Assert.True(named.Length > 2 * plain.Length, $"{named.Length} against {plain.Length} bytes");

        double perPlain = PerReplay(plain) / 2_048.0;
        double perNamed = PerReplay(named) / 2_048.0;
        Assert.True(
            Math.Abs(perNamed - perPlain) < 1,
            string.Create(CultureInfo.InvariantCulture, $"{perPlain:F1} B a record without names, {perNamed:F1} B with them"));
    }

    private static long PerReplay(MemoryStream journal)
    {
        long least = long.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = JournalV1Reader.ReplayBatches(journal, (_, _, _, _) => { });
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    private static void Check(List<string> report, string record, string step, double expected, double measured)
    {
        if (measured != expected)
        {
            report.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{record}: {step} allocates {measured:F2} B a call where what it hands on takes {expected:F2} B"));
        }
    }

    /// <summary>
    /// What one warm call allocates on this thread: the least of five runs of 64 calls. One run disturbed by a collection
    /// is not the call's own, and an object of the smallest size allocated on one call in 64 still shows.
    /// </summary>
    private static double PerCall(Action call)
    {
        for (int warm = 0; warm < 64; warm++)
        {
            call();
        }

        long least = long.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 64; index++)
            {
                call();
            }

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least / 64.0;
    }

    /// <summary>
    /// A capture's descriptors as the catalog compiles them, and a record of each kind its journal holds: transfers over
    /// IPv4 and IPv6, a named pipe's write with its name, status, identifier and extended items, an RPC call whose bytes
    /// the source does not expose, a classic event told apart by its opcode, and a transfer whose size the record lost.
    /// </summary>
    private sealed class Journal
    {
        private static readonly Guid Tcp = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

        private readonly AdmittedEventPlan transfer = new()
        {
            SourceIndex = 0,
            ProviderGuid = Tcp,
            EventId = 10,
            Version = 0,
            Name = "TCPv4 data sent",
            Mechanism = Mechanism.Tcp,
            Layer = ObservationLayer.Transport,
            Kind = ObservationKind.Send,
            Direction = Direction.Outbound,
            MinimumBodyLength = 28,
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
                new("connid", FieldRole.CorrelationKey, 20, 8, null, null, SlotTransform.None) { SourceField = SourceField.ConnectionId },
            ],
            FieldReport =
            [
                new("PID", FieldAvailability.Present, FieldRole.ProcessAttribution),
                new("size", FieldAvailability.Present, FieldRole.ByteCount, Unit: MeasurementUnit.Bytes, ByteDomain: ByteDomain.TransportObserved),
            ],
        };

        private readonly AdmittedEventPlan ipv6;
        private readonly AdmittedEventPlan pipe;
        private readonly AdmittedEventPlan rpc;
        private readonly AdmittedEventPlan classicSend;
        private readonly AdmittedEventPlan classicReceive;

        public Journal()
        {
            ipv6 = transfer with
            {
                SourceIndex = 1,
                EventId = 27,
                Name = "TCPv6 data received",
                Kind = ObservationKind.Receive,
                Direction = Direction.Inbound,
                MinimumBodyLength = 44,
                SchemaFingerprint = "sha256:fixture-transfer-v6",
                Slots =
                [
                    new("PID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None),
                    new("size", FieldRole.ByteCount, 4, 4, MeasurementUnit.Bytes, ByteDomain.TransportObserved, SlotTransform.None),
                    new("saddr", FieldRole.SourceEndpoint, 24, 16, null, null, SlotTransform.NetworkOrderIpv6Address, AdmittedSlotKind.Address128),
                    new("sport", FieldRole.SourceEndpoint, 42, 2, null, null, SlotTransform.NetworkOrderPort),
                    new("daddr", FieldRole.DestinationEndpoint, 8, 16, null, null, SlotTransform.NetworkOrderIpv6Address, AdmittedSlotKind.Address128),
                    new("dport", FieldRole.DestinationEndpoint, 40, 2, null, null, SlotTransform.NetworkOrderPort),
                ],
            };
            pipe = new()
            {
                SourceIndex = 2,
                ProviderGuid = Guid.Parse("33333333-3333-4333-8333-333333333333"),
                EventId = 16,
                Version = 1,
                Name = "Pipe write",
                Mechanism = Mechanism.NamedPipe,
                Layer = ObservationLayer.Resource,
                Kind = ObservationKind.Send,
                Direction = Direction.Outbound,
                MinimumBodyLength = 20,
                PointerSize = 8,
                SchemaFingerprint = "sha256:fixture-pipe",
                BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                Slots =
                [
                    new("PID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None),
                    new("IoSize", FieldRole.ByteCount, 4, 4, MeasurementUnit.Bytes, ByteDomain.RequestedIo, SlotTransform.None),
                    new("Status", FieldRole.Status, 8, 4, null, null, SlotTransform.None),
                    new("Irp", FieldRole.CorrelationKey, 12, 8, null, null, SlotTransform.None) { SourceField = SourceField.IoRequestPacket },
                ],
                FieldReport =
                [
                    new("PID", FieldAvailability.Present, FieldRole.ProcessAttribution),
                    new("Status", FieldAvailability.Present, FieldRole.Status),
                ],
            };
            rpc = new()
            {
                SourceIndex = 3,
                ProviderGuid = Guid.Parse("44444444-4444-4444-8444-444444444444"),
                EventId = 5,
                Version = 1,
                Name = "RPC client call",
                Mechanism = Mechanism.Rpc,
                Layer = ObservationLayer.Application,
                Kind = ObservationKind.RequestStart,
                Direction = Direction.Outbound,
                MinimumBodyLength = 4,
                PointerSize = 8,
                SchemaFingerprint = "sha256:fixture-rpc",
                BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                Slots =
                [
                    new("ProcNum", FieldRole.CorrelationKey, 0, 4, null, null, SlotTransform.None) { SourceField = SourceField.RpcProcedureNumber },
                ],
                FieldReport =
                [
                    new("Bytes", FieldAvailability.Present, FieldRole.ByteCount, Unit: MeasurementUnit.Bytes, ByteDomain: ByteDomain.ApplicationPayload),
                    new("Status", FieldAvailability.NotExposed, FieldRole.Status),
                ],
            };
            classicSend = new()
            {
                SourceIndex = 4,
                ProviderGuid = Guid.Parse("55555555-5555-4555-8555-555555555555"),
                EventId = 0,
                Version = 2,
                Opcode = 10,
                Name = "UDP send",
                Mechanism = Mechanism.Udp,
                Layer = ObservationLayer.Transport,
                Kind = ObservationKind.Send,
                Direction = Direction.Outbound,
                MinimumBodyLength = 8,
                PointerSize = 4,
                SchemaFingerprint = "sha256:fixture-classic",
                BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                Slots =
                [
                    new("PID", FieldRole.ProcessAttribution, 0, 4, null, null, SlotTransform.None),
                    new("size", FieldRole.ByteCount, 4, 4, MeasurementUnit.Bytes, ByteDomain.TransportObserved, SlotTransform.None),
                ],
                FieldReport = [new("PID", FieldAvailability.Present, FieldRole.ProcessAttribution)],
            };
            classicReceive = classicSend with { Opcode = 11, Name = "UDP receive", Kind = ObservationKind.Receive, Direction = Direction.Inbound };
            Clock = new(
                ClockId.Derive("decode-allocation-tests"),
                HostId.Derive("decode-allocation-tests"),
                SourceClockKind.Monotonic,
                TimestampEncoding.Qpc,
                10_000_000,
                0,
                TimestampRounding.NearestEven,
                SourceClockMath.SessionTicksPerSecond * 3_600);
            SourceAdmissionPlan[] sources =
            [
                .. new[] { transfer, ipv6, pipe, rpc, classicSend, classicReceive }
                    .GroupBy(descriptor => descriptor.SourceIndex)
                    .Select(group => new SourceAdmissionPlan
                    {
                        SourceId = $"fixture/source/{group.Key}",
                        ProviderGuid = group.First().ProviderGuid,
                        SourceIndex = group.Key,
                        Events = [.. group],
                        Diagnostics = [],
                    }),
            ];
            Mapper = new(sources, Clock.Id);
            Plan = JournalNormalizationPlanV1.Decode(JournalNormalizationPlanV1.FromSources(sources).Encode());
            Plan.ValidateAgainst(Mapper.Schemas);
            Normalizer = new(Clock);
        }

        public SourceClockDescriptor Clock { get; }

        public CaptureId Capture { get; } = CaptureId.New();

        public AdmittedEventEnvelopeMapper Mapper { get; }

        public JournalNormalizationPlanV1 Plan { get; }

        public ObservationNormalizerV1 Normalizer { get; }

        public IEnumerable<(string Record, AdmittedEventPlan Descriptor, AdmittedEvent Admitted)> Records()
        {
            AdmittedEvent sent = Admitted(transfer);
            Slots(ref sent, 4_242, 1_460, 0x0100007F, 0x5000, 0x0200007F, 0xBB01, 77);
            yield return ("a TCP send", transfer, sent);

            AdmittedEvent received = Admitted(ipv6);
            Slots(ref received, 4_242, 512, 0, 0x5000, 0, 0xBB01);
            received.SetAddress(0, UInt128.MaxValue - 1);
            received.SetAddress(1, (UInt128)0x2001_0db8 << 96);
            yield return ("a TCPv6 receive", ipv6, received);

            AdmittedEvent written = Admitted(pipe);
            Slots(ref written, 4_242, 4_096, 0, 0x7FFF_0000_1234);
            written.SetName(@"\Device\NamedPipe\lsass", truncated: false);
            written.SetIdentifier(Guid.Parse("12345678-1234-4234-8234-123456789abc"));
            written.BeginExtendedData(ExtendedDataAvailability.Captured, 3);
            Assert.True(written.TryAppendExtendedItem(EtwExtendedDataTypes.ProcessStartKey, 0, [1, 2, 3, 4, 5, 6, 7, 8], 8));
            Assert.True(written.TryAppendExtendedItem(EtwExtendedDataTypes.Sid, 0, [9, 9, 9, 9], 4));
            Assert.True(written.TryAppendExtendedItem(EtwExtendedDataTypes.QpcDelta, 1, [1, 0, 0, 0], 4));
            yield return ("a named pipe write", pipe, written);

            AdmittedEvent called = Admitted(rpc);
            Slots(ref called, 12);
            yield return ("an RPC call", rpc, called);

            AdmittedEvent classic = Admitted(classicReceive);
            Slots(ref classic, 4_242, 99);
            yield return ("a classic UDP receive", classicReceive, classic);

            AdmittedEvent lost = Admitted(transfer);
            Slots(ref lost, 4_242);
            lost.SetSlot(2, 0x0100007F);
            yield return ("a TCP send whose size was lost", transfer, lost);
        }

        /// <summary>A journal of TCP sends in batches of 16, with or without a long name on every record.</summary>
        public MemoryStream Written(int records, bool named)
        {
            var stream = new MemoryStream();
            using (JournalV1Writer writer = JournalV1Writer.Create(stream, Capture, Clock, DateTimeOffset.UnixEpoch, batchCapacity: 16))
            {
                writer.WriteSchemas(Mapper.Schemas);
                for (int index = 0; index < records; index++)
                {
                    AdmittedEvent admitted = Admitted(transfer);
                    admitted.RecordOrdinal = index + 1;
                    Slots(ref admitted, 4_242, 100 + index, 0x0100007F, 0x5000, 0x0200007F, 0xBB01, index);
                    if (named)
                    {
                        admitted.SetName(new string('\u4E2D', AdmittedEvent.MaximumNameLength), truncated: true);
                    }

                    writer.Append(Mapper.ToEnvelope(admitted, transfer, Capture));
                }

                writer.Complete();
            }

            return stream;
        }

        private static AdmittedEvent Admitted(AdmittedEventPlan plan) => new()
        {
            SourceIndex = plan.SourceIndex,
            EventId = plan.EventId,
            Version = plan.Version,
            Opcode = plan.Opcode ?? 0,
            TimestampQpc = 123_456,
            TimestampUtcTicks = 638_000_000_000_000_000,
            HeaderProcessId = 4_242,
            HeaderThreadId = 4_243,
            ProcessorNumber = 3,
            RecordOrdinal = 99,
            ActivityId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        };

        private static void Slots(ref AdmittedEvent admitted, params long[] values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                admitted.SetSlot(index, values[index]);
            }
        }
    }
}
