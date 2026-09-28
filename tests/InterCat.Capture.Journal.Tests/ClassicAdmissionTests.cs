using System.Diagnostics;
using System.Text;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// ADR-035: ALPC's classic send and receive, from the class's layout to the journal and back. The two share a class, an
/// id of 0 and a version, so every key that tells them apart carries the opcode.
/// </summary>
public sealed class ClassicAdmissionTests
{
    private static readonly Guid KernelProvider = Guid.Parse("9e814aad-3204-11d2-9a82-006008a86939");

    [Fact(DisplayName = "§18.3: ALPC's send and receive compile from the class's layout into two plans told apart by opcode, with one fingerprint")]
    public void AlpcCompilesIntoTwoPlansByOpcode()
    {
        SourceAdmissionPlan source = AlpcSource();

        Assert.Empty(source.Diagnostics);
        Assert.Equal(2, source.Events.Count);
        AdmittedEventPlan send = source.Events.Single(plan => plan.Opcode == 33);
        AdmittedEventPlan receive = source.Events.Single(plan => plan.Opcode == 34);
        Assert.Equal((WindowsSourceCatalog.AlpcEventClass, 0, 2), (send.ProviderGuid, send.EventId, send.Version));
        Assert.Equal((Mechanism.Alpc, ObservationLayer.Transport, ObservationKind.Send, Direction.Outbound),
            (send.Mechanism, send.Layer, send.Kind, send.Direction));
        Assert.Equal((ObservationKind.Receive, Direction.Inbound), (receive.Kind, receive.Direction));
        AdmittedSlotPlan slot = Assert.Single(send.Slots);
        Assert.Equal(("MessageID", 0, 4, (SourceField?)SourceField.AlpcMessageId), (slot.FieldName, slot.Offset, slot.Width, slot.SourceField));

        // One layout, one fingerprint: the journal allows a class one schema entry at a version.
        Assert.Equal(send.SchemaFingerprint, receive.SchemaFingerprint);
    }

    [Fact(DisplayName = "§18.3: the admission table tells a classic record by its class and opcode, and admits nothing else of the class")]
    public void TheTableResolvesAClassicRecordByOpcode()
    {
        SourceAdmissionPlan source = AlpcSource();
        var table = new EventAdmissionTable([source]);
        Guid alpc = WindowsSourceCatalog.AlpcEventClass;

        Assert.Equal(33, table.Resolve(alpc, 0, 2, 33).Plan?.Opcode);
        Assert.Equal(34, table.Resolve(alpc, 0, 2, 34).Plan?.Opcode);
        Assert.Equal(DescriptorAdmissionOutcome.DescriptorNotAdmitted, table.Resolve(alpc, 0, 2, 35).Outcome);
        Assert.Equal(DescriptorAdmissionOutcome.UnknownDescriptorVersion, table.Resolve(alpc, 0, 3, 33).Outcome);
        Assert.Equal(DescriptorAdmissionOutcome.UnrequestedProvider, table.Resolve(KernelProvider, 65535, 2, 33).Outcome);
        Assert.Equal(34, table.FindBySourceIndex(0, 0, 2, 34)?.Opcode);
        Assert.Null(table.FindBySourceIndex(0, 0, 2, 36));
    }

    [Fact(DisplayName = "§18.3: a classic record is named by its header's class, id 0 and opcode, whatever provider a decoder reports for it")]
    public void AClassicRecordIsNamedByItsClass()
    {
        Guid thread = Guid.Parse("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        Guid manifest = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

        // A thread rundown record is reported under the generic kernel provider by one consumer and under no provider by
        // another; both are the Thread class's opcode 3, as ALPC's send is its class's opcode 33.
        Assert.Equal((thread, 0, 3), EventAdmissionTable.RecordIdentity(true, KernelProvider, thread, 65535, 3));
        Assert.Equal((thread, 0, 3), EventAdmissionTable.RecordIdentity(true, Guid.Empty, thread, 0, 3));
        Assert.Equal((WindowsSourceCatalog.AlpcEventClass, 0, 33),
            EventAdmissionTable.RecordIdentity(true, KernelProvider, WindowsSourceCatalog.AlpcEventClass, 65535, 33));
        Assert.Equal((manifest, 5, EventAdmissionTable.ManifestOpcode), EventAdmissionTable.RecordIdentity(false, manifest, Guid.Empty, 5, 1));

        // The classes a system logger delivers unasked are named for a display, as a kernel flag group's class is.
        Assert.Equal("Kernel thread events (Thread class)", WindowsSourceCatalog.ClassicEventClassName(thread));
        Assert.Equal("Kernel ALPC (EVENT_TRACE_FLAG_ALPC)", WindowsSourceCatalog.ClassicEventClassName(WindowsSourceCatalog.AlpcEventClass));
        Assert.Null(WindowsSourceCatalog.ClassicEventClassName(manifest));
    }

    [Fact(DisplayName = "§18.3: a journal holds one schema entry for ALPC's send and receive, and a replay picks each record's plan by its opcode")]
    public void AJournalKeepsTheOpcodeThatNamesTheRecord()
    {
        SourceAdmissionPlan source = AlpcSource();
        AdmittedEventPlan send = source.Events.Single(plan => plan.Opcode == 33);
        AdmittedEventPlan receive = source.Events.Single(plan => plan.Opcode == 34);
        var mapper = new AdmittedEventEnvelopeMapper([source], ClockId.New());
        Assert.Single(mapper.Schemas.Schemas);

        AdmittedEvent received = Admitted(opcode: 34);
        received.SetSlot(0, 7_777);
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(received, receive, CaptureId.New());
        Assert.Equal((WindowsSourceCatalog.AlpcEventClass, (ushort)0, (byte)2, (byte)34),
            (envelope.Header.ProviderId, envelope.Header.EventId, envelope.Header.Version, envelope.Header.Opcode));

        JournalNormalizationPlanV1 plan = JournalNormalizationPlanV1.Decode(JournalNormalizationPlanV1.FromSources([source]).Encode());
        plan.ValidateAgainst(mapper.Schemas);
        Assert.Equal(34, plan.Resolve(envelope, mapper.Schemas).Opcode);
        AdmittedEvent replayed = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, receive);
        Assert.True(replayed.TryGetSlot(0, out long messageId));
        Assert.Equal(7_777, messageId);

        // A send's record is refused against the receive's plan, and the other way round.
        Assert.Throws<InvalidDataException>(() => mapper.ToEnvelope(Admitted(opcode: 33), receive, CaptureId.New()));
        Assert.Throws<InvalidDataException>(() => AdmittedEventEnvelopeMapper.FromEnvelope(envelope, send));
    }

    [Fact(DisplayName = "§18.3: a plan file names a classic descriptor's opcode and leaves a manifest descriptor's plan as it was")]
    public void APlanFileCarriesTheOpcodeOnlyWhereItIsIdentity()
    {
        string classic = Encoding.UTF8.GetString(JournalNormalizationPlanV1.FromSources([AlpcSource()]).Encode());
        Assert.Contains("\"opcode\":33", classic, StringComparison.Ordinal);
        Assert.Contains("\"opcode\":34", classic, StringComparison.Ordinal);

        SourceAdmissionPlan manifest = AlpcSource() with
        {
            Events = [.. AlpcSource().Events.Take(1).Select(plan => plan with { Opcode = null, ProviderGuid = Guid.NewGuid() })],
        };
        Assert.DoesNotContain("opcode", Encoding.UTF8.GetString(JournalNormalizationPlanV1.FromSources([manifest]).Encode()),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§18.3: a live capture enables ALPC's kernel flags, publishes its rows with message ids, and a ledger that tells send from receive")]
    public async Task ALiveCapturePublishesAlpcRowsAndCoverageByOpcode()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "alpc-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        Guid alpc = WindowsSourceCatalog.AlpcEventClass;
        AdmittedEvent Message(int opcode, int ordinal, int process)
        {
            AdmittedEvent message = Admitted(opcode);
            message.TimestampQpc = now + ordinal;
            message.TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            message.HeaderProcessId = process;
            message.RecordOrdinal = ordinal;
            message.SetSlot(0, 4_242);
            return message;
        }

        // A client sends a message and its server receives it; ALPC's waits are delivered too, and admitted by no plan.
        host.Admit(Message(33, 1, 100), new DeliveredRecord(alpc, 0, 2, now + 1, 33));
        host.Admit(Message(34, 2, 300), new DeliveredRecord(alpc, 0, 2, now + 2, 34));
        host.Omit(new DeliveredRecord(alpc, 0, 2, now + 3, 36), OmissionReason.DescriptorNotAdmitted);
        OwnedSessionPlan plan = new()
        {
            Identity = CaptureSessionIdentity.Create("test", 4242),
            Providers = [],
            Sources = [AlpcSource()],
        };

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(plan, host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        Assert.True(result.Start.Started);
        ProviderEnablementResult kernel = Assert.Single(result.Start.Providers);
        Assert.Equal((WindowsSourceCatalog.KernelAlpcSourceId, true), (kernel.SourceId, kernel.Enabled));
        Assert.Equal(2, result.JournaledRecords);

        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        ObservationRowV1[] rows =
        [
            .. SessionSegments.Names(manifest)
                .Select(name => SessionSegments.Open(reopened.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.Row))
                .OrderBy(row => row.NativeTicks),
        ];
        Assert.Equal(
            [(Mechanism.Alpc, ObservationKind.Send, Direction.Outbound, (byte)33, 100), (Mechanism.Alpc, ObservationKind.Receive, Direction.Inbound, (byte)34, 300)],
            rows.Select(row => (row.Mechanism, row.Kind, row.Direction, row.Opcode, row.HeaderProcessId)));
        SourceFieldRowV1[] fields =
        [
            .. SessionSegments.FieldNames(manifest)
                .Select(name => SessionSegments.Open(reopened.Root, manifest, name))
                .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow)),
        ];
        Assert.Equal(2, fields.Length);
        Assert.All(fields, field => Assert.Equal((SourceField.AlpcMessageId, (long?)4_242), (field.Field, field.Value)));

        // The ledger collected send and receive as two descriptors of one class, and counts the waits as not admitted.
        CoverageEpochV1 epoch = Assert.Single(SessionSegments.CoverageLedger(reopened.Root, manifest)!.Epochs);
        Assert.Equal(
            [(alpc, 0, 2, (int?)33, Mechanism.Alpc), (alpc, 0, 2, (int?)34, Mechanism.Alpc)],
            epoch.Collected.Select(descriptor => (descriptor.ProviderId, descriptor.EventId, descriptor.Version, descriptor.Opcode, descriptor.Mechanism)));
        Assert.All(epoch.Collected, descriptor => Assert.Equal("Kernel ALPC (EVENT_TRACE_FLAG_ALPC)", descriptor.ProviderName));
        Assert.Equal(
            [((int?)33, 1L, 1L, 0L, (OmissionReason?)null), (34, 1, 1, 0, null), (36, 1, 0, 1, OmissionReason.DescriptorNotAdmitted)],
            epoch.Deliveries.Select(delivery => (delivery.Opcode, delivery.Delivered, delivery.Admitted, delivery.Omitted, delivery.Omission)));
    }

    [Fact(DisplayName = "§18.3: a coverage tally names a classic record's descriptor by its opcode and a manifest record's without one")]
    public void ATallyKeepsTheOpcodeOnlyWhereItIsIdentity()
    {
        OwnedSessionPlan plan = EvidenceRecordings.Plan() with
        {
            Sources = [.. EvidenceRecordings.Plan().Sources, AlpcSource() with { SourceIndex = 1, Events = [.. AlpcSource().Events.Select(descriptor => descriptor with { SourceIndex = 1 })] }],
        };
        var tally = new CaptureCoverageTally(plan, CoverageAcquisition.LiveCapture);
        Guid alpc = WindowsSourceCatalog.AlpcEventClass;
        var sent = new DeliveredRecord(alpc, 0, 2, 1_000, 33);
        var manifest = new DeliveredRecord(EvidenceRecordings.Provider, 10, 0, 1_001);
        tally.Delivered(in sent);
        tally.Admitted(alpc, 0, 2, 33, queued: true);
        tally.Delivered(in manifest);
        tally.Admitted(EvidenceRecordings.Provider, 10, 0, EventAdmissionTable.ManifestOpcode, queued: true);

        CoverageEpochV1 epoch = Assert.Single(tally.ToLedger(
        [
            new() { Layer = LossLayer.SourceSession, Lost = 0 },
            new() { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new() { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new() { Layer = LossLayer.Storage, Lost = 0 },
        ]).Epochs);
        Assert.Equal([(int?)null, 33, 34], epoch.Collected.OrderBy(descriptor => descriptor.Opcode ?? -1).Select(descriptor => descriptor.Opcode));
        Assert.Equal(
            [(EvidenceRecordings.Provider, (int?)null, 1L), (alpc, 33, 1)],
            epoch.Deliveries.OrderBy(delivery => delivery.Opcode ?? -1).Select(delivery => (delivery.ProviderId, delivery.Opcode, delivery.Admitted)));
    }

    /// <summary>ALPC's source compiled from the layout this machine registers for it (ADR-035's addendum).</summary>
    private static SourceAdmissionPlan AlpcSource()
    {
        ProviderSchemaEvent Message(int opcode) =>
            new(0, 2, null, "ALPC", null, null, opcode, null, [], 0, null, [ProviderSchemaField.Create("MessageID", "win:UInt32")]);
        ProviderSchemaEvent[] events = [Message(33), Message(34), Message(35)];
        var schema = new ProviderSchema("ALPC", WindowsSourceCatalog.AlpcEventClass,
            TdhClassicSchemaReader.Fingerprint(WindowsSourceCatalog.AlpcEventClass, 2, 8, events), new Dictionary<string, ulong>(), events);
        return AdmissionPlanCompiler.Compile(WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelAlpcSourceId)!, schema, 0);
    }

    private static AdmittedEvent Admitted(int opcode) => new()
    {
        SourceIndex = 0,
        EventId = 0,
        Version = 2,
        Opcode = opcode,
        PointerSize = 8,
        TimestampQpc = 1_000,
        HeaderProcessId = 100,
        HeaderThreadId = 200,
        RecordOrdinal = 1,
    };
}
