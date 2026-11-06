using InterCat.Capture.Recording;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Sections' schema-failure case (M6's exit tests, ADR-090's fifth addendum): a capture's compilation refuses either kernel
/// source of the opt-in sections profile where its gate could not decide its records - on a build none measured, or by a
/// layout without a field its gate decides by - so the profile does not start, the source blocking with the refusal's reason,
/// and the capability report says its schema is unknown, for that reason, rather than start and follow no section.
/// </summary>
public sealed class SectionsProfileRefusalTests
{
    private const string Measured = "10.0.26220.0-x64";
    private const string Unmeasured = "10.0.19045.0-x64";
    private const string Views = WindowsSourceCatalog.KernelFileViewsSourceId;
    private const string Handles = WindowsSourceCatalog.KernelSectionHandlesSourceId;
    private static readonly string[] Kernel = [Views, Handles];

    [Fact(DisplayName = "R3: a capture's compilation refuses the file view flag and sections' handles where their gates could not decide their records - on a build none measured, or a layout without a field a gate decides by - so the sections profile does not start, and icat capabilities says its schema is unknown")]
    public void AnUndecidableSectionSourceDoesNotStartTheProfile()
    {
        // The machine registers the object manager's class and no file I/O layout at the version this build raises views at.
        var whole = new CapabilityInventoryProbe(new Machine(HandleLayout(), null));
        var withoutProcess = new CapabilityInventoryProbe(new Machine(HandleLayout(), ViewLayout(withoutProcess: true)));
        var withoutHandle = new CapabilityInventoryProbe(new Machine(HandleLayout(withoutCreationsHandle: true), null));

        // On its measured build both compile - the views by the layout measured for the build, which a warning states - and
        // the profile starts.
        SourcePlanCompilation compiled = whole.CompilePlanSetOn(Measured, Kernel);
        Assert.Equal(Kernel, compiled.Plans.Select(static plan => plan.SourceId));
        Assert.DoesNotContain(compiled.Issues, static issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        Assert.True(Sections(compiled).CanStart);

        foreach ((CapabilityInventoryProbe inventory, string build, string source, string expected) in new (CapabilityInventoryProbe, string, string, string)[]
        {
            (whole, Unmeasured, Views, "The machine registers no layout for this classic event class at the version it is planned for, and none is measured for this build."),
            (whole, Unmeasured, Handles, "No Section type number is measured for build 10.0.19045.0-x64"),
            (withoutProcess, Measured, Views, "admits no ProcessId for opcode 37 v3 (View mapped)"),
            (withoutHandle, Measured, Handles, "admits no Handle for opcode 32 v2 (Section handle created)"),
        })
        {
            // A capture's compilation refuses the source with its gate's reason, and compiles no plan of it.
            SourcePlanCompilation refused = inventory.CompilePlanSetOn(build, Kernel);
            SourcePlanIssue refusal = Assert.Single(refused.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal && issue.SourceId == source);
            Assert.Contains(expected, refusal.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(refused.Plans, plan => plan.SourceId == source);

            // So the sections profile does not start, the source blocking with that reason, rather than start and record
            // lifecycle and half of each section.
            EffectiveCapturePlan profile = Sections(refused);
            Assert.False(profile.CanStart);
            ProfileSourceDecision decision = Assert.Single(profile.SourceDecisions, item => item.SourceId == source);
            Assert.Equal((ProfileSourceDecisionState.Blocking, true, refusal.Reason), (decision.State, decision.Required, decision.Reason));

            // And the capability report says its schema is unknown, for that reason.
            SourceCapability reported = inventory.Probe(Environment(build)).Sources.Single(item => item.SourceId == source);
            Assert.Equal((CapabilityState.SchemaUnknown, refusal.Reason), (reported.State, reported.UnavailableReason));
        }

        // A profile compiled against the machine's inventory compiles its sources for the build it is compiled for: on a build
        // none measured neither kernel source is admitted, each blocking with its own reason.
        EffectiveCapturePlan unmeasured = CaptureProfileCompiler.Compile(new(CaptureProfileKind.Sections), whole, Environment(Unmeasured));
        Assert.False(unmeasured.CanStart);
        Assert.Contains(unmeasured.SourceDecisions, static item => item.SourceId == Views && item.State == ProfileSourceDecisionState.Blocking
            && item.Reason.Contains("none is measured for this build", StringComparison.Ordinal));
        Assert.Contains(unmeasured.SourceDecisions, static item => item.SourceId == Handles && item.State == ProfileSourceDecisionState.Blocking
            && item.Reason.Contains("No Section type number is measured for build 10.0.19045.0-x64", StringComparison.Ordinal));
    }

    /// <summary>The sections profile compiled with lifecycle beside the kernel sources' compilation: it asks for all three.</summary>
    private static EffectiveCapturePlan Sections(SourcePlanCompilation kernel)
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
            new(CaptureProfileKind.Sections),
            new SourcePlanCompilation(
                [lifecycle, .. kernel.Plans.Select(static (plan, index) => plan with
                {
                    SourceIndex = index + 1,
                    Events = [.. plan.Events.Select(descriptor => descriptor with { SourceIndex = index + 1 })],
                })],
                kernel.Issues));
    }

    private static ProbeEnvironment Environment(string build) => CapabilityInventoryProbe.DescribeEnvironment(isElevated: false) with { BuildId = build };

    /// <summary>The layout this build registers for the object manager's class (§4.2, revision 928), or one without a creation's handle.</summary>
    private static ProviderSchema HandleLayout(bool withoutCreationsHandle = false)
    {
        static ProviderSchemaField Field(string name, string type) => ProviderSchemaField.Create(name, type);
        static ProviderSchemaEvent Event(int opcode, params ProviderSchemaField[] fields) => new(0, 2, null, "Object", null, null, opcode, null, [], 0, null, fields);
        ProviderSchemaField[] handle = [Field("Object", "win:Pointer"), Field("Handle", "win:UInt32"), Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString")];
        ProviderSchemaEvent[] events =
        [
            withoutCreationsHandle
                ? Event(ObjectHandleGate.Create, Field("Object", "win:Pointer"), Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString"))
                : Event(ObjectHandleGate.Create, handle),
            Event(ObjectHandleGate.Close, handle),
            Event(ObjectHandleGate.Duplicate, Field("Object", "win:Pointer"), Field("SourceHandle", "win:UInt32"), Field("TargetHandle", "win:UInt32"),
                Field("TargetProcessId", "win:UInt32"), Field("ObjectType", "win:UInt16")),
            Event(ObjectHandleGate.TypeRundown, Field("ObjectType", "win:UInt16"), Field("Reserved", "win:UInt16"), Field("TypeName", "win:UnicodeString")),
            Event(ObjectHandleGate.Rundown, Field("Object", "win:Pointer"), Field("ProcessId", "win:UInt32"), Field("Handle", "win:UInt32"),
                Field("ObjectType", "win:UInt16"), Field("ObjectName", "win:UnicodeString")),
        ];
        return new ProviderSchema("Object manager", WindowsSourceCatalog.ObjectManagerEventClass,
            TdhClassicSchemaReader.Fingerprint(WindowsSourceCatalog.ObjectManagerEventClass, 2, 8, events), new Dictionary<string, ulong>(), events);
    }

    /// <summary>The measured view layout's fields as a machine that registered version 3 would describe them, without the process.</summary>
    private static ProviderSchema ViewLayout(bool withoutProcess)
    {
        ProviderSchemaEvent[] events = [.. MeasuredClassicLayouts.Find(Measured, WindowsSourceCatalog.FileIoEventClass, "File I/O", 3,
            [SectionViewGate.Map, SectionViewGate.Unmap, SectionViewGate.StartRundown, SectionViewGate.StopRundown])!.Events.Select(descriptor => withoutProcess
                ? descriptor with { Fields = [.. descriptor.Fields.Where(static field => field.Name != "ProcessId")] }
                : descriptor)];
        return new ProviderSchema("File I/O", WindowsSourceCatalog.FileIoEventClass,
            TdhClassicSchemaReader.Fingerprint(WindowsSourceCatalog.FileIoEventClass, 3, 8, events), new Dictionary<string, ulong>(), events);
    }

    /// <summary>
    /// A machine that registers the object manager's class with the handle layout given and the file I/O class with the view
    /// layout given, or none at the version asked for, and no manifest provider.
    /// </summary>
    private sealed class Machine(ProviderSchema handles, ProviderSchema? views) : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) =>
            classGuid == WindowsSourceCatalog.ObjectManagerEventClass ? handles
            : classGuid == WindowsSourceCatalog.FileIoEventClass ? views
            : null;
    }
}
