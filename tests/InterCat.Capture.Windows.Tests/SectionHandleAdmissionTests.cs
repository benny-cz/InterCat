using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Sections' handles (ADR-090): the object manager's handle group read for the Section type alone, a source of its own beside
/// the synchronization objects' lead over the same group, whose gate writes each section's number over its address - never an
/// address kept, and never the number its views are followed by.
/// </summary>
public sealed class SectionHandleAdmissionTests
{
    private const string Measured = "10.0.26220.0-x64";
    private const string Unmeasured = "10.0.19045.0-x64";
    private const int Create = ObjectHandleGate.Create;
    private const int Close = ObjectHandleGate.Close;
    private const int Duplicate = ObjectHandleGate.Duplicate;
    private const int TypeRundown = ObjectHandleGate.TypeRundown;
    private const int Rundown = ObjectHandleGate.Rundown;
    private const int Section = 49;
    private const int Event = 21;
    private const ulong Address = 0xFFFF_C000_0000_1000;
    private const ulong OtherAddress = 0xFFFF_C000_0000_2000;

    [Fact(DisplayName = "R22: sections' handle events compile from the object manager's registered layout to fixed slots - the object, which the gate writes the section's number over, the handle, the type, a close's and a rundown's name, a duplication's handles and the target that owns it - each a shared section's record")]
    public void TheSectionHandleEventsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal([Create, Close, Duplicate, TypeRundown, Rundown], plan.Events.Select(static descriptor => descriptor.Opcode!.Value).Order());
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.SharedSection, ObservationLayer.Resource, 2), (descriptor.Mechanism, descriptor.Layer, descriptor.Version));
            (string Field, int Offset)[] expected = descriptor.Opcode switch
            {
                Create => [("Object", 0), ("Handle", 8), ("ObjectType", 12)],
                Close => [("Object", 0), ("Handle", 8), ("ObjectType", 12), ("ObjectName", 14)],
                Duplicate => [("Object", 0), ("SourceHandle", 8), ("TargetHandle", 12), ("TargetProcessId", 16), ("ObjectType", 20)],
                TypeRundown => [("ObjectType", 0), ("TypeName", 4)],
                _ => [("Object", 0), ("ProcessId", 8), ("Handle", 12), ("ObjectType", 16), ("ObjectName", 18)],
            };
            Assert.Equal(expected, descriptor.Slots.Select(static slot => (slot.FieldName, slot.Offset)).OrderBy(static slot => slot.Offset));
        }

        // The object is the section's number, never an address, and never a synchronization object's or a view's section's.
        Assert.All(plan.Events.SelectMany(static descriptor => descriptor.Slots).Where(static slot => slot.FieldName == "Object"),
            static slot => Assert.Equal((FieldRole.CorrelationKey, (SourceField?)SourceField.SectionObjectNumber), (slot.Role, slot.SourceField)));
        Assert.All(plan.Events.SelectMany(static descriptor => descriptor.Slots).Where(static slot => slot.FieldName is "Handle" or "TargetHandle"),
            static slot => Assert.Equal((FieldRole.ResourceHandle, (SourceField?)SourceField.HandleValue), (slot.Role, slot.SourceField)));
        Assert.All(plan.Events.SelectMany(static descriptor => descriptor.Slots).Where(static slot => slot.FieldName == "ObjectType"),
            static slot => Assert.Equal((SourceField?)SourceField.ObjectTypeNumber, slot.SourceField));
        Assert.Contains(Plan(plan, Duplicate).Slots, static slot => slot is { FieldName: "TargetProcessId", Role: FieldRole.ProcessAttribution, SourceField: null });
        Assert.Contains(Plan(plan, Rundown).Slots, static slot => slot is { FieldName: "ProcessId", Role: FieldRole.ProcessAttribution });
    }

    [Fact(DisplayName = "R22: through its gate the sections' handle source admits a section's handle records with its number written over its address, both processes' handles one section, and omits an event's, a kernel handle's and those beyond its bound; the type rundown admits the Section type's entry alone")]
    public void TheGateAdmitsWithTheSectionsNumber()
    {
        SourceAdmissionPlan plan = Compiled();

        // The gate a capture on this build makes for the source follows the Section type measured for it, and no other.
        ObjectHandleRecordGate built = Assert.IsType<ObjectHandleRecordGate>(Assert.Single(RecordGates.For(new EventAdmissionTable([plan]))));
        AdmittedEvent sectionsHandle = Record(plan, Create, 400, ("Object", unchecked((long)Address)), ("Handle", 0x30), ("ObjectType", Section));
        AdmittedEvent eventsHandle = Record(plan, Create, 400, ("Object", unchecked((long)OtherAddress)), ("Handle", 0x34), ("ObjectType", Event));
        Assert.Equal(((GateOmission?)null, (GateOmission?)GateOmission.NoFollowedResource),
            (built.Read(ref sectionsHandle, Plan(plan, Create)), built.Read(ref eventsHandle, Plan(plan, Create))));
        var gate = new ObjectHandleRecordGate([Section]);

        // The creator's handle and the opener's, at the section's one address: one section, its number never the address.
        AdmittedEvent created = Record(plan, Create, 400, ("Object", unchecked((long)Address)), ("Handle", 0x10), ("ObjectType", Section));
        Assert.Null(gate.Read(ref created, Plan(plan, Create)));
        AdmittedEvent opened = Record(plan, Create, 500, ("Object", unchecked((long)Address)), ("Handle", 0x24), ("ObjectType", Section));
        Assert.Null(gate.Read(ref opened, Plan(plan, Create)));
        Assert.Equal((1L, 1L), (Slot(plan, created, "Object"), Slot(plan, opened, "Object")));
        AdmittedEvent closed = Record(plan, Close, 500, ("Object", unchecked((long)Address)), ("Handle", 0x24), ("ObjectType", Section));
        Assert.Null(gate.Read(ref closed, Plan(plan, Close)));
        AdmittedEvent closedToo = Record(plan, Close, 400, ("Object", unchecked((long)Address)), ("Handle", 0x10), ("ObjectType", Section));
        Assert.Null(gate.Read(ref closedToo, Plan(plan, Close)));
        Assert.Equal((1L, 1L), (Slot(plan, closed, "Object"), Slot(plan, closedToo, "Object")));

        // Its address's next section is another; an event's handle is of no object the gate follows, nor a kernel handle.
        AdmittedEvent next = Record(plan, Create, 400, ("Object", unchecked((long)Address)), ("Handle", 0x10), ("ObjectType", Section));
        Assert.Null(gate.Read(ref next, Plan(plan, Create)));
        Assert.Equal(2L, Slot(plan, next, "Object"));
        AdmittedEvent anEvent = Record(plan, Create, 400, ("Object", unchecked((long)OtherAddress)), ("Handle", 0x14), ("ObjectType", Event));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref anEvent, Plan(plan, Create)));
        AdmittedEvent kernel = Record(plan, Create, 400, ("Object", unchecked((long)OtherAddress)), ("Handle", unchecked((int)0x8000_0010)), ("ObjectType", Section));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref kernel, Plan(plan, Create)));

        // At the stop, a section's open handle named by the rundown's own process, and the type rundown's Section entry alone.
        AdmittedEvent open = Record(plan, Rundown, 4, ("Object", unchecked((long)OtherAddress)), ("ProcessId", 600), ("Handle", 0x20), ("ObjectType", Section));
        Assert.Null(gate.Read(ref open, Plan(plan, Rundown)));
        Assert.Equal((3L, 600L), (Slot(plan, open, "Object"), Slot(plan, open, "ProcessId")));
        AdmittedEvent sectionType = Record(plan, TypeRundown, 4, ("ObjectType", Section));
        Assert.Null(gate.Read(ref sectionType, Plan(plan, TypeRundown)));
        AdmittedEvent eventType = Record(plan, TypeRundown, 4, ("ObjectType", Event));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref eventType, Plan(plan, TypeRundown)));

        // A gate holding as many handles as it may begins no more sections.
        var full = new ObjectHandleRecordGate([Section], capacity: 1);
        AdmittedEvent first = Record(plan, Create, 400, ("Object", unchecked((long)Address)), ("Handle", 0x10), ("ObjectType", Section));
        Assert.Null(full.Read(ref first, Plan(plan, Create)));
        AdmittedEvent second = Record(plan, Create, 400, ("Object", unchecked((long)OtherAddress)), ("Handle", 0x14), ("ObjectType", Section));
        Assert.Equal(GateOmission.BeyondBound, full.Read(ref second, Plan(plan, Create)));
    }

    [Fact(DisplayName = "R22: an admitted section handle record is a shared section row of the section mapping family, its section's number, type and handle source fields, and the process that raised a creation its owner, as its payload names none")]
    public void AnAdmittedSectionHandleRecordIsASectionRow()
    {
        SourceAdmissionPlan plan = Compiled();
        SourceClockDescriptor clock = new(
            ClockId.Derive("section-handle-admission-tests"),
            HostId.Derive("section-handle-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        var normalizer = new ObservationNormalizerV1(clock);
        var gate = new ObjectHandleRecordGate([Section]);

        // A creation raised in process 400, its address written over by the gate: the section's row, its raiser's since
        // `process-binding-v8`.
        AdmittedEvent created = Record(plan, Create, 400, ("Object", unchecked((long)Address)), ("Handle", 0x10), ("ObjectType", Section));
        Assert.Null(gate.Read(ref created, Plan(plan, Create)));
        created.RecordOrdinal = 1;
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(created, Plan(plan, Create), CaptureId.New());
        ObservationRowV1 row = normalizer.ToRow(envelope, Plan(plan, Create));
        Assert.Equal((Mechanism.SharedSection, ObservationLayer.Resource, ObservationKind.Open), (row.Mechanism, row.Layer, row.Kind));
        Assert.Equal(((int?)null, 400), (row.OwnerProcessId, row.HeaderProcessId));
        Assert.Equal(400, RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId));
        Assert.Equal(
            [(SourceField.ObjectTypeNumber, 49L), (SourceField.HandleValue, 0x10L), (SourceField.SectionObjectNumber, 1L)],
            ObservationNormalizerV1.FieldRows(envelope, Plan(plan, Create), row).Select(static field => (field.Field, field.Value!.Value)).OrderBy(static field => field.Field));
        Assert.Equal("section-mapping", ObservationNormalizerV1.DiscriminatorFor(Mechanism.SharedSection));
        Assert.Null(row.Validate());
    }

    [Fact(DisplayName = "R3: a build with no measured Section number refuses sections' handle source, as does a layout without a field its gate decides by")]
    public void AnUndecidableSectionHandleSourceIsRefused()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Null(RecordGates.Undecidable(plan, Measured));
        Assert.Contains("No Section type number is measured for build 10.0.19045.0-x64", RecordGates.Undecidable(plan, Unmeasured), StringComparison.Ordinal);
        Assert.Contains("admits no Handle for opcode 32 v2 (Section handle created)", RecordGates.Undecidable(Compiled(withoutCreationsHandle: true), Measured),
            StringComparison.Ordinal);

        // A capture's compilation refuses it there, with the gate's reason, and compiles no plan of it.
        SourcePlanCompilation refused = new CapabilityInventoryProbe(new ClassMetadata(Layout())).CompilePlanSetOn(Unmeasured, [WindowsSourceCatalog.KernelSectionHandlesSourceId]);
        Assert.Empty(refused.Plans);
        Assert.Contains("No Section type number is measured", Assert.Single(refused.Issues, static issue => issue.Severity == SourcePlanIssueSeverity.Refusal).Reason,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§9.4: no capture collects both of the handle group's sources - they compile the same descriptors, which an admission table refuses - and no profile asks for both")]
    public void NoCaptureCollectsBothHandleSources()
    {
        SourceAdmissionPlan sections = Compiled();
        SourceAdmissionPlan synchronization = AdmissionPlanCompiler.Compile(WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelObjectHandlesSourceId)!, Layout(), 1);
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => new EventAdmissionTable([sections, synchronization]));
        Assert.Contains("compiled more than once", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures), static profile =>
            profile.Sources.Any(static source => source.SourceId == WindowsSourceCatalog.KernelObjectHandlesSourceId)
            && profile.Sources.Any(static source => source.SourceId == WindowsSourceCatalog.KernelSectionHandlesSourceId));
    }

    /// <summary>Sections' handle source compiled from the layout this build registers for the object manager's class.</summary>
    private static SourceAdmissionPlan Compiled(bool withoutCreationsHandle = false) =>
        AdmissionPlanCompiler.Compile(WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelSectionHandlesSourceId)!, Layout(withoutCreationsHandle), 0);

    /// <summary>The layout this build registers for the object manager's class (§4.2, revision 928), or one without a creation's handle.</summary>
    private static ProviderSchema Layout(bool withoutCreationsHandle = false)
    {
        static ProviderSchemaField Field(string name, string type) => ProviderSchemaField.Create(name, type);
        static ProviderSchemaEvent Event(int opcode, params ProviderSchemaField[] fields) => new(0, 2, null, "Object", null, null, opcode, null, [], 0, null, fields);
        ProviderSchemaField[] handle = [Field("Object", "win:Pointer"), Field("Handle", "win:UInt32"), Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString")];
        ProviderSchemaEvent[] events =
        [
            withoutCreationsHandle ? Event(Create, Field("Object", "win:Pointer"), Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString")) : Event(Create, handle),
            Event(Close, handle),
            Event(Duplicate, Field("Object", "win:Pointer"), Field("SourceHandle", "win:UInt32"), Field("TargetHandle", "win:UInt32"),
                Field("TargetProcessId", "win:UInt32"), Field("ObjectType", "win:UInt16")),
            Event(TypeRundown, Field("ObjectType", "win:UInt16"), Field("Reserved", "win:UInt16"), Field("TypeName", "win:UnicodeString")),
            Event(Rundown, Field("Object", "win:Pointer"), Field("ProcessId", "win:UInt32"), Field("Handle", "win:UInt32"),
                Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString")),
        ];
        return new ProviderSchema("Object manager", WindowsSourceCatalog.ObjectManagerEventClass,
            TdhClassicSchemaReader.Fingerprint(WindowsSourceCatalog.ObjectManagerEventClass, 2, 8, events), new Dictionary<string, ulong>(), events);
    }

    /// <summary>A machine that registers the object manager's class with the layout given, and no manifest provider.</summary>
    private sealed class ClassMetadata(ProviderSchema layout) : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) =>
            classGuid == WindowsSourceCatalog.ObjectManagerEventClass ? layout : null;
    }

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int opcode) => plan.Events.Single(descriptor => descriptor.Opcode == opcode);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int opcode, int raisedBy, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, opcode);
        var admitted = new AdmittedEvent { SourceIndex = descriptor.SourceIndex, EventId = 0, Version = 2, Opcode = opcode, PointerSize = 8, HeaderProcessId = raisedBy };
        foreach ((string field, long value) in fields)
        {
            admitted.SetSlot(Index(descriptor, field), value);
        }

        return admitted;
    }

    private static long Slot(SourceAdmissionPlan plan, AdmittedEvent admitted, string field)
    {
        Assert.True(admitted.TryGetSlot(Index(Plan(plan, admitted.Opcode), field), out long value), field);
        return value;
    }

    private static int Index(AdmittedEventPlan descriptor, string field)
    {
        for (int index = 0; index < descriptor.Slots.Count; index++)
        {
            if (descriptor.Slots[index].FieldName == field)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"{field} is not admitted by opcode {descriptor.Opcode}.");
    }
}
