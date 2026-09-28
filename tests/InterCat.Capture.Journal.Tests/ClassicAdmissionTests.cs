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
