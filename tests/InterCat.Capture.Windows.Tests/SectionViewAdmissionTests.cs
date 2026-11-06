using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The file view flag's records admitted (ADR-090, ADR-091): this build raises a view's mapping, unmapping and both rundowns at a
/// version the machine registers no layout for, so they compile by the layout measured for the build, said so in the plan, and
/// admit through the section view gate, each view's number written over its base and its section's over the value its views
/// carry - never an address kept.
/// </summary>
public sealed class SectionViewAdmissionTests
{
    private const string Measured = "10.0.26220.0-x64";
    private const string Unmeasured = "10.0.19045.0-x64";
    private const int Map = SectionViewGate.Map;
    private const int Unmap = SectionViewGate.Unmap;
    private const int StartRundown = SectionViewGate.StartRundown;
    private const int StopRundown = SectionViewGate.StopRundown;
    private static readonly string[] Views = [WindowsSourceCatalog.KernelFileViewsSourceId];

    [Fact(DisplayName = "§18.3: TDH describes the file view events at version 2 alone on this build - a view's base, file object, extra information, size and process - and none at version 3, the version the build raises them at")]
    public void TdhDescribesTheViewEventsAtVersion2Alone()
    {
        var tdh = new TdhEtwMetadataSource();
        Assert.Null(tdh.TryReadClassic(WindowsSourceCatalog.FileIoEventClass, "File I/O", 3, [Map, Unmap, StartRundown, StopRundown]));
        ProviderSchema? registered = tdh.TryReadClassic(WindowsSourceCatalog.FileIoEventClass, "File I/O", 2, [Map, Unmap, StartRundown, StopRundown]);
        Assert.NotNull(registered);
        Assert.All(registered!.Events, descriptor => Assert.Equal(["ViewBase", "FileObject", "MiscInfo", "ViewSize", "ProcessId"],
            descriptor.Fields.Select(static field => field.Name)));
        Assert.Equal([Map, Unmap, StartRundown, StopRundown], registered.Events.Select(static descriptor => descriptor.OpcodeValue!.Value).Order());
    }

    [Fact(DisplayName = "R22: on its measured build the file view flag's four events compile by the layout measured for it - the view, its section, its extra information and size at their measured offsets, and the process whose view it is at the record's end - its plan saying so, and its fingerprint of the measured scheme, never a registered layout's")]
    public void TheViewEventsCompileByTheMeasuredLayout()
    {
        SourcePlanCompilation compiled = new CapabilityInventoryProbe(new Registering(null)).CompilePlanSetOn(Measured, Views);
        SourceAdmissionPlan plan = Assert.Single(compiled.Plans);
        Assert.Empty(plan.Diagnostics);
        Assert.DoesNotContain(compiled.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        SourcePlanIssue stated = Assert.Single(compiled.Issues);
        Assert.Equal((SourcePlanIssueSeverity.Warning, MeasuredClassicLayouts.Statement(Measured, 3)), (stated.Severity, stated.Reason));
        Assert.Contains("read by the layout measured for build 10.0.26220.0-x64 (ADR-091)", stated.Reason, StringComparison.Ordinal);

        Assert.Equal([Map, Unmap, StartRundown, StopRundown], plan.Events.Select(static descriptor => descriptor.Opcode!.Value).Order());
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.SharedSection, ObservationLayer.Resource, 3), (descriptor.Mechanism, descriptor.Layer, descriptor.Version));
            Assert.Equal(descriptor.Opcode switch { Map => ObservationKind.Map, Unmap => ObservationKind.Unmap, _ => ObservationKind.Inventory }, descriptor.Kind);
            (string, int, SourceField?)[] expected =
            [
                ("ViewBase", 0, SourceField.SectionViewNumber), ("FileObject", 8, SourceField.SectionNumber), ("MiscInfo", 16, SourceField.ViewInformation),
                ("ViewSize", 24, SourceField.ViewSize), ("ProcessId", 40, null),
            ];
            Assert.Equal(expected, descriptor.Slots.OrderBy(static slot => slot.Offset).Select(static slot => (slot.FieldName, slot.Offset, slot.SourceField)));
            Assert.Equal(44, descriptor.MinimumBodyLength);

            // A view's size and information are never bytes moved; its base and value are written over by the gate; the
            // process it names is the record's owner.
            Assert.DoesNotContain(descriptor.Slots, static slot => slot.Role == FieldRole.ByteCount);
            Assert.All(descriptor.Slots.Where(static slot => slot.FieldName is "ViewBase" or "FileObject"), static slot => Assert.Equal(FieldRole.CorrelationKey, slot.Role));
            Assert.Contains(descriptor.Slots, static slot => slot is { FieldName: "ProcessId", Role: FieldRole.ProcessAttribution });
        }

        // The fingerprint is of the measured scheme: the same fields registered at version 3 would compile to another.
        SourceAdmissionPlan registered = Assert.Single(new CapabilityInventoryProbe(new Registering(Registered())).CompilePlanSetOn(Measured, Views).Plans);
        Assert.All(plan.Events, descriptor => Assert.DoesNotContain(registered.Events,
            other => other.SchemaFingerprint == descriptor.SchemaFingerprint));
        Assert.Equal(plan.Events.Select(static descriptor => descriptor.Slots.Count), registered.Events.Select(static descriptor => descriptor.Slots.Count));
    }

    [Fact(DisplayName = "R3: on a build none measured the file view flag is refused - the machine registers no layout at the version the build raises, and none is measured for it - and where the machine registers that version its registration is read, nothing said to be measured")]
    public void AnUnmeasuredBuildRefusesTheViews()
    {
        SourcePlanCompilation refused = new CapabilityInventoryProbe(new Registering(null)).CompilePlanSetOn(Unmeasured, Views);
        Assert.Empty(refused.Plans);
        SourcePlanIssue refusal = Assert.Single(refused.Issues);
        Assert.Equal(SourcePlanIssueSeverity.Refusal, refusal.Severity);
        Assert.Contains("registers no layout for this classic event class at the version it is planned for, and none is measured", refusal.Reason, StringComparison.Ordinal);
        Assert.Null(MeasuredClassicLayouts.Find(Unmeasured, WindowsSourceCatalog.FileIoEventClass, "File I/O", 3, [Map]));
        Assert.Null(MeasuredClassicLayouts.Find(Measured, WindowsSourceCatalog.FileIoEventClass, "File I/O", 2, [Map]));
        Assert.Null(MeasuredClassicLayouts.Find(Measured, WindowsSourceCatalog.ObjectManagerEventClass, "Object manager", 3, [34, 37]));

        // A machine that registers the version is read by its registration, which always wins.
        SourcePlanCompilation registered = new CapabilityInventoryProbe(new Registering(Registered())).CompilePlanSetOn(Unmeasured, Views);
        Assert.Single(registered.Plans);
        Assert.Empty(registered.Issues);

        // A layout without the process a view is followed by: the gate could not tell whose view a record is, so it is refused.
        SourcePlanCompilation without = new CapabilityInventoryProbe(new Registering(Registered(withoutProcess: true))).CompilePlanSetOn(Measured, Views);
        Assert.Empty(without.Plans);
        Assert.Contains("admits no ProcessId for opcode 37 v3 (View mapped)", Assert.Single(without.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: through its gate the file view flag admits each view's records with the view's number over its base and its section's over the value its views carry, each view the process's its record names, a rundown's though no header names one, and omits a view beyond the gate's bound")]
    public void TheGateAdmitsWithTheViewsAndSectionsNumbers()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.IsType<SectionViewRecordGate>(Assert.Single(RecordGates.For(new EventAdmissionTable([plan]))));
        Assert.Null(RecordGates.Undecidable(plan, Measured));
        var gate = new SectionViewRecordGate();

        // The start's rundown names the creator's view, mapped before the capture, in its record; the header names no process.
        AdmittedEvent seeded = Record(plan, StartRundown, 0, ("ViewBase", 0x1_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_1000)), ("MiscInfo", 0x00C4_0000_0000_0000),
            ("ViewSize", 65_536), ("ProcessId", 400));
        Assert.Null(gate.Read(ref seeded, Plan(plan, StartRundown)));
        Assert.Equal((1L, 1L, 400L), (Slot(plan, seeded, "ViewBase"), Slot(plan, seeded, "FileObject"), Slot(plan, seeded, "ProcessId")));

        // The opener maps the section at the same base in its own process: another view, the same section; its size and
        // information kept as raised.
        AdmittedEvent opened = Record(plan, Map, 500, ("ViewBase", 0x1_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_1000)), ("MiscInfo", 0x00C4_0000_0000_0000),
            ("ViewSize", 4_096), ("ProcessId", 500));
        Assert.Null(gate.Read(ref opened, Plan(plan, Map)));
        Assert.Equal((2L, 1L, 4_096L, 0x00C4_0000_0000_0000L), (Slot(plan, opened, "ViewBase"), Slot(plan, opened, "FileObject"), Slot(plan, opened, "ViewSize"), Slot(plan, opened, "MiscInfo")));
        AdmittedEvent closed = Record(plan, Unmap, 500, ("ViewBase", 0x1_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_1000)), ("MiscInfo", 0x00C4_0000_0000_0000),
            ("ViewSize", 4_096), ("ProcessId", 500));
        Assert.Null(gate.Read(ref closed, Plan(plan, Unmap)));
        Assert.Equal((2L, 1L, 500L), (Slot(plan, closed, "ViewBase"), Slot(plan, closed, "FileObject"), Slot(plan, closed, "ProcessId")));

        // The stop's rundown of the creator's view, still mapped, is that view; an image's view another section.
        AdmittedEvent stopped = Record(plan, StopRundown, 0, ("ViewBase", 0x1_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_1000)), ("MiscInfo", 0x00C4_0000_0000_0000),
            ("ViewSize", 65_536), ("ProcessId", 400));
        Assert.Null(gate.Read(ref stopped, Plan(plan, StopRundown)));
        Assert.Equal((1L, 1L), (Slot(plan, stopped, "ViewBase"), Slot(plan, stopped, "FileObject")));
        AdmittedEvent image = Record(plan, Map, 400, ("ViewBase", 0x7FF0_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_2000)), ("MiscInfo", 0x0047_0000_0000_0000),
            ("ViewSize", 1_048_576), ("ProcessId", 400));
        Assert.Null(gate.Read(ref image, Plan(plan, Map)));
        Assert.Equal((3L, 2L), (Slot(plan, image, "ViewBase"), Slot(plan, image, "FileObject")));

        // The creator's view at its base unmapped, the record its process's: the start's rundown's view, not the opener's.
        AdmittedEvent unmapped = Record(plan, Unmap, 400, ("ViewBase", 0x1_0000), ("FileObject", unchecked((long)0xFFFF_A000_0000_1000)), ("MiscInfo", 0x00C4_0000_0000_0000),
            ("ViewSize", 65_536), ("ProcessId", 400));
        Assert.Null(gate.Read(ref unmapped, Plan(plan, Unmap)));
        Assert.Equal((1L, 1L), (Slot(plan, unmapped, "ViewBase"), Slot(plan, unmapped, "FileObject")));

        // A gate following as many views as it may begins no more.
        var full = new SectionViewRecordGate(capacity: 1);
        AdmittedEvent first = Record(plan, Map, 400, ("ViewBase", 0x1_0000), ("FileObject", 0x1000), ("ProcessId", 400));
        Assert.Null(full.Read(ref first, Plan(plan, Map)));
        AdmittedEvent second = Record(plan, Map, 400, ("ViewBase", 0x2_0000), ("FileObject", 0x1000), ("ProcessId", 400));
        Assert.Equal(GateOmission.BeyondBound, full.Read(ref second, Plan(plan, Map)));
    }

    [Fact(DisplayName = "§4.2: the layout measured for the file view flag is the one its committed reports measured on its build - version 3, 44 bytes, eight page-aligned bytes between the size and the process at the record's end - for a view's mapping and unmapping and both rundowns")]
    public void TheMeasuredLayoutIsItsEvidences()
    {
        MeasuredClassicLayout layout = Assert.Single(MeasuredClassicLayouts.All);
        Assert.Equal((Measured, WindowsSourceCatalog.FileIoEventClass, 3), (layout.BuildId, layout.ClassGuid, layout.Version));
        Assert.Equal([Map, Unmap, StartRundown, StopRundown], layout.Opcodes.Select(static opcode => opcode.Opcode));
        Assert.Equal(["ViewBase", "FileObject", "MiscInfo", "ViewSize", "Offset", "ProcessId"], layout.Fields.Select(static field => field.Name));
        Assert.Equal(44, layout.Fields.Sum(static field => field.WidthKind == FieldWidthKind.PointerSized ? 8 : field.FixedWidth));

        // Its evidence: the three measurements of FX-SECT-001's mappings and unmappings, and the three of both rundowns.
        string root = RepositoryRoot();
        Assert.Equal(6, layout.Evidence.Count);
        var opcodesMeasured = new HashSet<int>();
        foreach (string evidence in layout.Evidence)
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, evidence)));
            JsonElement[] layouts = evidence.Contains("section-rundowns-", StringComparison.Ordinal)
                ? [report.RootElement.GetProperty("rundowns").GetProperty("start"), report.RootElement.GetProperty("rundowns").GetProperty("stop")]
                : [report.RootElement.GetProperty("viewLayout")];
            foreach (JsonElement measured in layouts)
            {
                int[] Values(string property) => [.. measured.GetProperty(property).EnumerateArray().Select(static value => value.GetInt32())];
                Assert.Equal([3], Values("versions"));
                Assert.Equal([44], Values("payloadBytes"));
                Assert.Equal([8], Values("betweenSizeAndProcessBytes"));
                Assert.True(measured.GetProperty("betweenPageAligned").GetBoolean(), evidence);
                if (measured.TryGetProperty("opcode", out JsonElement opcode))
                {
                    opcodesMeasured.Add(opcode.GetInt32());
                }
                else
                {
                    opcodesMeasured.UnionWith([Map, Unmap]);
                }
            }
        }

        Assert.Equal([Map, Unmap, StartRundown, StopRundown], opcodesMeasured.Order());
    }

    /// <summary>The file view flag compiled from the layout measured for its build.</summary>
    private static SourceAdmissionPlan Compiled() =>
        Assert.Single(new CapabilityInventoryProbe(new Registering(null)).CompilePlanSetOn(Measured, Views).Plans);

    /// <summary>The measured layout's fields as a machine that registered version 3 would describe them, or without the process.</summary>
    private static ProviderSchema Registered(bool withoutProcess = false)
    {
        ProviderSchemaEvent[] events = [.. MeasuredClassicLayouts.Find(Measured, WindowsSourceCatalog.FileIoEventClass, "File I/O", 3,
            [Map, Unmap, StartRundown, StopRundown])!.Events.Select(descriptor => withoutProcess
                ? descriptor with { Fields = [.. descriptor.Fields.Where(static field => field.Name != "ProcessId")] }
                : descriptor)];
        return new ProviderSchema("File I/O", WindowsSourceCatalog.FileIoEventClass,
            TdhClassicSchemaReader.Fingerprint(WindowsSourceCatalog.FileIoEventClass, 3, 8, events), new Dictionary<string, ulong>(), events);
    }

    /// <summary>A machine that registers the file I/O class with the layout given at any version asked for, or none, and no manifest provider.</summary>
    private sealed class Registering(ProviderSchema? layout) : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) =>
            classGuid == WindowsSourceCatalog.FileIoEventClass ? layout : null;
    }

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int opcode) => plan.Events.Single(descriptor => descriptor.Opcode == opcode);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int opcode, int raisedBy, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, opcode);
        var admitted = new AdmittedEvent { SourceIndex = descriptor.SourceIndex, EventId = 0, Version = 3, Opcode = opcode, PointerSize = 8, HeaderProcessId = raisedBy };
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

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
