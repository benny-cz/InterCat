using InterCat.Domain;
using Xunit;
using static InterCat.Capture.Windows.SynchronizationHandleGate;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The object manager handle group's admission (ADR-083's addendum): its handle events compile from the class's layout to
/// fixed slots, and admit through the synchronization handle gate, which keeps only the synchronization types' records and
/// writes each object's number over its address, so no admitted record keeps one.
/// </summary>
public sealed class ObjectHandleAdmissionTests
{
    private const int Event = 21;
    private const int Mutant = 22;
    private const int Semaphore = 24;
    private const int File = 37;
    private const long Address = unchecked((long)0xFFFF_A000_0000_1000);
    private const long OtherAddress = unchecked((long)0xFFFF_A000_0000_2000);

    [Fact(DisplayName = "§18.3: TDH describes the object manager's handle events from a header alone - a creation's and a close's object, handle, type and name, a duplication's handles and target, and the stop's type and handle rundowns")]
    public void TdhDescribesTheHandleEvents()
    {
        ProviderSchema? schema = new TdhEtwMetadataSource().TryReadClassic(WindowsSourceCatalog.ObjectManagerEventClass, "Object manager", 2,
            [Create, Close, Duplicate, TypeRundown, Rundown]);
        Assert.NotNull(schema);
        Assert.Equal(
            [
                "32: Object win:Pointer, Handle win:UInt32, ObjectType win:UInt16, ObjectName win:UnicodeString",
                "33: Object win:Pointer, Handle win:UInt32, ObjectType win:UInt16, ObjectName win:UnicodeString",
                "34: Object win:Pointer, SourceHandle win:UInt32, TargetHandle win:UInt32, TargetProcessId win:UInt32, ObjectType win:UInt16",
                "37: ObjectType win:UInt16, Reserved win:UInt16, TypeName win:UnicodeString",
                "39: Object win:Pointer, ProcessId win:UInt32, Handle win:UInt32, ObjectType win:UInt16, ObjectName win:UnicodeString",
            ],
            schema!.Events.OrderBy(descriptor => descriptor.OpcodeValue).Select(descriptor =>
                $"{descriptor.OpcodeValue}: {string.Join(", ", descriptor.Fields.Select(field => field.Name + " " + field.InType))}"));
    }

    [Fact(DisplayName = "R22: the handle group's events compile from the class's layout to fixed slots - the object, which the gate writes its number over, the handle, the type, a close's and a rundown's name, a duplication's handles and the target that owns it - and nothing else")]
    public void TheHandleEventsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal([Create, Close, Duplicate, TypeRundown, Rundown], plan.Events.Select(descriptor => descriptor.Opcode!.Value).Order());
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.Synchronization, ObservationLayer.Resource), (descriptor.Mechanism, descriptor.Layer));
            (string Field, int Offset)[] slots = [.. descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset)).OrderBy(slot => slot.Offset)];
            (string, int)[] expected = descriptor.Opcode switch
            {
                Create => [("Object", 0), ("Handle", 8), ("ObjectType", 12)],
                Close => [("Object", 0), ("Handle", 8), ("ObjectType", 12), ("ObjectName", 14)],
                Duplicate => [("Object", 0), ("SourceHandle", 8), ("TargetHandle", 12), ("TargetProcessId", 16), ("ObjectType", 20)],
                TypeRundown => [("ObjectType", 0), ("TypeName", 4)],
                _ => [("Object", 0), ("ProcessId", 8), ("Handle", 12), ("ObjectType", 16), ("ObjectName", 18)],
            };
            Assert.Equal(expected, slots);
        }

        // The object is the gate's number, never an address; a handle's value is its owner's - a duplication's, the handle it
        // made in the target its record names, which owns it as an open coming in, and the handle it was duplicated from a
        // process no record names; a name is read as one where a record carries it.
        Assert.All(plan.Events.SelectMany(descriptor => descriptor.Slots).Where(slot => slot.FieldName == "Object"),
            slot => Assert.Equal((FieldRole.CorrelationKey, (SourceField?)SourceField.SynchronizationObjectNumber), (slot.Role, slot.SourceField)));
        Assert.All(plan.Events.SelectMany(descriptor => descriptor.Slots).Where(slot => slot.FieldName is "Handle" or "TargetHandle"),
            slot => Assert.Equal((FieldRole.ResourceHandle, (SourceField?)SourceField.HandleValue), (slot.Role, slot.SourceField)));
        Assert.All(plan.Events.SelectMany(descriptor => descriptor.Slots).Where(slot => slot.FieldName == "ObjectType"),
            slot => Assert.Equal((SourceField?)SourceField.ObjectTypeNumber, slot.SourceField));
        AdmittedEventPlan duplicate = Plan(plan, Duplicate);
        Assert.Equal((ObservationKind.Open, Direction.Inbound), (duplicate.Kind, duplicate.Direction));
        Assert.Contains(duplicate.Slots, slot => slot is { FieldName: "SourceHandle", Role: FieldRole.ResourceHandle, SourceField: SourceField.PeerHandleValue });
        Assert.Contains(duplicate.Slots, slot => slot is { FieldName: "TargetProcessId", Role: FieldRole.ProcessAttribution, SourceField: null });
        Assert.All(plan.Events.SelectMany(descriptor => descriptor.Slots).Where(slot => slot.FieldName is "ObjectName" or "TypeName"),
            slot => Assert.Equal((FieldRole.ResourceName, AdmittedSlotKind.ResourceName), (slot.Role, slot.Kind)));
        Assert.Contains(Plan(plan, Rundown).Slots, slot => slot is { FieldName: "ProcessId", Role: FieldRole.ProcessAttribution });
    }

    [Fact(DisplayName = "R22: through its gate the handle group admits a synchronization type's handle records with the object's number written over its address, a duplication's for its target, and omits another type's, a kernel handle's and those beyond its bound")]
    public void TheGateAdmitsWithTheObjectsNumber()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.IsType<ObjectHandleRecordGate>(Assert.Single(RecordGates.For(new EventAdmissionTable([plan]))));
        var gate = new ObjectHandleRecordGate([Event, Mutant, Semaphore]);

        // An event's handle created in process 100: admitted, its object the gate's first, never its address.
        AdmittedEvent created = Record(plan, Create, 100, ("Object", Address), ("Handle", 0x10), ("ObjectType", Event));
        Assert.Null(gate.Read(ref created, Plan(plan, Create)));
        Assert.Equal((1L, 0x10L), (Slot(plan, created, "Object"), Slot(plan, created, "Handle")));

        // Duplicated into process 200, then closed in both: one object throughout.
        AdmittedEvent duplicated = Record(plan, Duplicate, 100, ("Object", Address), ("SourceHandle", 0x10), ("TargetHandle", 0x88),
            ("TargetProcessId", 200), ("ObjectType", Event));
        Assert.Null(gate.Read(ref duplicated, Plan(plan, Duplicate)));
        Assert.Equal((1L, 200L), (Slot(plan, duplicated, "Object"), Slot(plan, duplicated, "TargetProcessId")));
        AdmittedEvent closed = Record(plan, Close, 100, ("Object", Address), ("Handle", 0x10), ("ObjectType", Event));
        Assert.Null(gate.Read(ref closed, Plan(plan, Close)));
        AdmittedEvent closedThere = Record(plan, Close, 200, ("Object", Address), ("Handle", 0x88), ("ObjectType", Event));
        Assert.Null(gate.Read(ref closedThere, Plan(plan, Close)));
        Assert.Equal((1L, 1L), (Slot(plan, closed, "Object"), Slot(plan, closedThere, "Object")));

        // A file's handle is of no object the gate follows; at the stop, a mutant's open handle named by the rundown's own
        // process is another object's, and the type rundown admits the synchronization types' entries alone.
        AdmittedEvent file = Record(plan, Create, 100, ("Object", OtherAddress), ("Handle", 0x14), ("ObjectType", File));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref file, Plan(plan, Create)));
        AdmittedEvent open = Record(plan, Rundown, 4, ("Object", OtherAddress), ("ProcessId", 300), ("Handle", 0x20), ("ObjectType", Mutant));
        Assert.Null(gate.Read(ref open, Plan(plan, Rundown)));
        Assert.Equal((2L, 300L), (Slot(plan, open, "Object"), Slot(plan, open, "ProcessId")));
        AdmittedEvent eventType = Record(plan, TypeRundown, 4, ("ObjectType", Event));
        Assert.Null(gate.Read(ref eventType, Plan(plan, TypeRundown)));
        AdmittedEvent fileType = Record(plan, TypeRundown, 4, ("ObjectType", File));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref fileType, Plan(plan, TypeRundown)));

        // Both handles to the first object closed, its address begins another object at its next handle.
        AdmittedEvent reused = Record(plan, Create, 100, ("Object", Address), ("Handle", 0x10), ("ObjectType", Semaphore));
        Assert.Null(gate.Read(ref reused, Plan(plan, Create)));
        Assert.Equal(3L, Slot(plan, reused, "Object"));

        // A kernel handle to it, made and closed in a process the kernel ran for, is no record the gate admits, nor a
        // duplication into the system process's table, nor the stop's rundown of one; the object is followed unchanged.
        AdmittedEvent kernel = Record(plan, Create, 100, ("Object", Address), ("Handle", 0x8000_0010), ("ObjectType", Semaphore));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref kernel, Plan(plan, Create)));
        AdmittedEvent kernelClosed = Record(plan, Close, 500, ("Object", Address), ("Handle", 0x8000_0010), ("ObjectType", Semaphore));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref kernelClosed, Plan(plan, Close)));
        AdmittedEvent intoSystem = Record(plan, Duplicate, 100, ("Object", Address), ("SourceHandle", 0x10), ("TargetHandle", 0x8000_0024),
            ("TargetProcessId", 4), ("ObjectType", Semaphore));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref intoSystem, Plan(plan, Duplicate)));
        AdmittedEvent kernelOpen = Record(plan, Rundown, 600, ("Object", Address), ("ProcessId", 4), ("Handle", 0x8000_0030), ("ObjectType", Semaphore));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref kernelOpen, Plan(plan, Rundown)));

        // A duplication from a kernel handle into a process makes that process a handle of its own, the object's.
        AdmittedEvent outOfSystem = Record(plan, Duplicate, 100, ("Object", Address), ("SourceHandle", 0x8000_0040), ("TargetHandle", 0x44),
            ("TargetProcessId", 300), ("ObjectType", Semaphore));
        Assert.Null(gate.Read(ref outOfSystem, Plan(plan, Duplicate)));
        Assert.Equal((3L, 300L), (Slot(plan, outOfSystem, "Object"), Slot(plan, outOfSystem, "TargetProcessId")));

        // A gate holding as many objects as it may begins no more.
        var full = new ObjectHandleRecordGate([Event], capacity: 1);
        AdmittedEvent first = Record(plan, Create, 100, ("Object", Address), ("Handle", 0x10), ("ObjectType", Event));
        Assert.Null(full.Read(ref first, Plan(plan, Create)));
        AdmittedEvent second = Record(plan, Create, 100, ("Object", OtherAddress), ("Handle", 0x14), ("ObjectType", Event));
        Assert.Equal(GateOmission.BeyondBound, full.Read(ref second, Plan(plan, Create)));
    }

    [Fact(DisplayName = "R3: a build with no measured synchronization type numbers refuses the handle group, as does a schema without a field its gate decides by")]
    public void AnUndecidableHandleGroupIsRefused()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Null(RecordGates.Undecidable(plan, "10.0.26220.0-x64"));
        Assert.Contains("No synchronization type numbers are measured for build 10.0.19045.0-x64",
            RecordGates.Undecidable(plan, "10.0.19045.0-x64"), StringComparison.Ordinal);

        // A creation whose layout carries no handle: the gate could not hold one, so the source is refused.
        SourceAdmissionPlan without = Compiled(withoutCreationsHandle: true);
        Assert.Contains("admits no Handle for opcode 32 v2 (Handle created)", RecordGates.Undecidable(without, "10.0.26220.0-x64"), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a capture's compilation refuses the handle group where its gate could not decide its records - on a build with no measured type numbers, or a layout without a field the gate decides by - so the synchronization objects profile does not start, and icat capabilities says its schema is unknown")]
    public void AnUndecidableHandleGroupDoesNotStart()
    {
        const string Measured = "10.0.26220.0-x64";
        const string Unmeasured = "10.0.19045.0-x64";
        string[] group = [WindowsSourceCatalog.KernelObjectHandlesSourceId];
        var whole = new CapabilityInventoryProbe(new ClassMetadata(Layout()));
        var withoutHandle = new CapabilityInventoryProbe(new ClassMetadata(Layout(withoutCreationsHandle: true)));

        // On its measured build with the whole layout the group compiles, the profile starts, and the report keeps it a
        // kernel group the provider registry cannot confirm.
        SourcePlanCompilation compiled = whole.CompilePlanSetOn(Measured, group);
        Assert.Single(compiled.Plans);
        Assert.DoesNotContain(compiled.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        Assert.True(SyncObjects(compiled).CanStart);
        Assert.Equal(CapabilityState.Unsupported, Reported(whole, Measured).State);

        foreach ((CapabilityInventoryProbe inventory, string build, string expected) in new (CapabilityInventoryProbe, string, string)[]
        {
            (whole, Unmeasured, "No synchronization type numbers are measured for build 10.0.19045.0-x64"),
            (withoutHandle, Measured, "admits no Handle for opcode 32 v2 (Handle created)"),
        })
        {
            // A capture's compilation refuses the group with the gate's reason, and compiles no plan of it.
            SourcePlanCompilation refused = inventory.CompilePlanSetOn(build, group);
            Assert.Empty(refused.Plans);
            SourcePlanIssue refusal = Assert.Single(refused.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
            Assert.Contains(expected, refusal.Reason, StringComparison.Ordinal);

            // So the synchronization objects profile does not start, the group blocking with that reason, rather than start
            // and follow no object.
            EffectiveCapturePlan profile = SyncObjects(refused);
            Assert.False(profile.CanStart);
            ProfileSourceDecision decision = Assert.Single(profile.SourceDecisions, item => item.SourceId == WindowsSourceCatalog.KernelObjectHandlesSourceId);
            Assert.Equal((ProfileSourceDecisionState.Blocking, true, refusal.Reason), (decision.State, decision.Required, decision.Reason));

            // And the capability report says its schema is unknown, for that reason.
            SourceCapability reported = Reported(inventory, build);
            Assert.Equal((CapabilityState.SchemaUnknown, refusal.Reason), (reported.State, reported.UnavailableReason));
        }

        // A profile compiled against the machine's inventory compiles its sources for the build it is compiled for.
        EffectiveCapturePlan unmeasured = CaptureProfileCompiler.Compile(new(CaptureProfileKind.SyncObjects), whole, Environment(Unmeasured));
        Assert.False(unmeasured.CanStart);
        Assert.Contains(unmeasured.SourceDecisions, item => item.SourceId == WindowsSourceCatalog.KernelObjectHandlesSourceId
            && item.Reason.Contains("No synchronization type numbers are measured for build 10.0.19045.0-x64", StringComparison.Ordinal));
    }

    /// <summary>The synchronization objects profile compiled with lifecycle beside the handle group's compilation: it asks for both.</summary>
    private static EffectiveCapturePlan SyncObjects(SourcePlanCompilation handles)
    {
        Guid provider = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        var lifecycle = new SourceAdmissionPlan
        {
            SourceId = WindowsSourceCatalog.KernelProcessSourceId,
            ProviderGuid = provider,
            SourceIndex = 0,
            Events =
            [
                new AdmittedEventPlan
                {
                    SourceIndex = 0,
                    ProviderGuid = provider,
                    EventId = 1,
                    Version = 0,
                    Name = "fixture",
                    Mechanism = Mechanism.ProcessLifecycle,
                    Layer = ObservationLayer.Lifecycle,
                    Kind = ObservationKind.Create,
                    Direction = Direction.DirectionNotApplicable,
                    MinimumBodyLength = 0,
                    PointerSize = 8,
                    SchemaFingerprint = "sha256:fixture-lifecycle",
                    BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                    Slots = [],
                    FieldReport = [],
                },
            ],
            Diagnostics = [],
        };
        return CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.SyncObjects),
            new SourcePlanCompilation(
                [lifecycle, .. handles.Plans.Select(plan => plan with
                {
                    SourceIndex = 1,
                    Events = [.. plan.Events.Select(descriptor => descriptor with { SourceIndex = 1 })],
                })],
                handles.Issues));
    }

    private static SourceCapability Reported(CapabilityInventoryProbe inventory, string build) =>
        inventory.Probe(Environment(build)).Sources.Single(source => source.SourceId == WindowsSourceCatalog.KernelObjectHandlesSourceId);

    private static ProbeEnvironment Environment(string build) => CapabilityInventoryProbe.DescribeEnvironment(isElevated: false) with { BuildId = build };

    /// <summary>The handle group compiled from the layout this build registers for its class (§4.2, revision 928).</summary>
    private static SourceAdmissionPlan Compiled(bool withoutCreationsHandle = false) =>
        AdmissionPlanCompiler.Compile(WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelObjectHandlesSourceId)!, Layout(withoutCreationsHandle), 0);

    /// <summary>The layout this build registers for the object manager's class, or one without a creation's handle.</summary>
    private static ProviderSchema Layout(bool withoutCreationsHandle = false)
    {
        ProviderSchemaField Field(string name, string type) => ProviderSchemaField.Create(name, type);
        ProviderSchemaEvent Event(int opcode, params ProviderSchemaField[] fields) =>
            new(0, 2, null, "Object", null, null, opcode, null, [], 0, null, fields);
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
