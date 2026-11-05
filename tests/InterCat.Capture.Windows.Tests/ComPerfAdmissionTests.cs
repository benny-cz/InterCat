using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// COM-Perf's call events admitted (ADR-079's addendum): a synchronous call's starts and stops at both ends compile from the
/// provider's registered layout to fixed slots - its trace number, the process at its other end, its method and interface,
/// its HRESULT - and never to a thread, a pointer, an IPID, an OID or an address; an admitted record is a COM call row whose
/// payload names no owner, so the process that raised it owns it (since `process-binding-v5`), the process it names at its other
/// end a source field and never its owner.
/// </summary>
public sealed class ComPerfAdmissionTests
{
    // The provider's registered layout of its synchronous call events, as TDH gave it on Windows 10.0.26220 (ADR-077,
    // bench/results/com-provider-inspection-20261104T141555Z).
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-COM-Perf" guid="{b8d6861b-d20f-4eec-bbae-87e0dd80602b}">
           <templates>
            <template tid="ClientSyncCallStart">
             <data name="CallTraceId" inType="win:UInt32" /><data name="TargetProcessId" inType="win:UInt32" />
             <data name="TargetThreadId" inType="win:UInt32" /><data name="CausalityId" inType="win:GUID" />
             <data name="TargetMethod" inType="win:UInt32" /><data name="TargetInterface" inType="win:GUID" />
             <data name="ProxyInterfacePointer" inType="win:Pointer" /><data name="ProxyManagerIUnknown" inType="win:Pointer" />
             <data name="IPID" inType="win:GUID" /><data name="OID" inType="win:UInt64" />
             <data name="CallerLogicalThreadId" inType="win:GUID" /><data name="CallerReturnAddress" inType="win:Pointer" />
             <data name="WinrtAsyncPatternMethodIndicator" inType="win:UInt32" /><data name="FakeSync" inType="win:Boolean" />
            </template>
            <template tid="ClientSyncCallStop">
             <data name="CallTraceId" inType="win:UInt32" /><data name="HRESULT" inType="win:Int32" />
             <data name="SourceOfHRESULT" inType="win:UInt32" />
            </template>
            <template tid="ServerSyncCallStart">
             <data name="CallTraceId" inType="win:UInt32" /><data name="SourceProcessId" inType="win:UInt32" />
             <data name="SourceThreadId" inType="win:UInt32" /><data name="CausalityId" inType="win:GUID" />
             <data name="TargetMethod" inType="win:UInt32" /><data name="TargetInterface" inType="win:GUID" />
             <data name="InterfacePointer" inType="win:Pointer" /><data name="MethodAddress" inType="win:Pointer" />
             <data name="ServerIUnknown" inType="win:Pointer" /><data name="IPID" inType="win:GUID" />
             <data name="OID" inType="win:UInt64" /><data name="WinrtAsyncPatternMethodIndicator" inType="win:UInt32" />
            </template>
            <template tid="ServerSyncCallStop">
             <data name="CallTraceId" inType="win:UInt32" /><data name="HRESULT" inType="win:Int32" />
             <data name="ServerException" inType="win:UInt32" />
            </template>
           </templates>
           <events>
            <event value="39" version="1" level="win:Informational" template="ClientSyncCallStart" />
            <event value="40" version="0" level="win:Informational" template="ClientSyncCallStop" />
            <event value="43" version="2" level="win:Informational" template="ServerSyncCallStart" />
            <event value="44" version="0" level="win:Informational" template="ServerSyncCallStop" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    private static readonly Guid Dispatch = Guid.Parse("00020400-0000-0000-c000-000000000046");

    private static WindowsSourceDefinition Source => WindowsSourceCatalog.Find(WindowsSourceCatalog.ComPerfSourceId)!;

    private static SourceAdmissionPlan Compiled() => AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), 0);

    [Fact(DisplayName = "R22: COM-Perf's call events compile from its registered layout to fixed slots - a call's trace number, the other end's process, its method and interface, its HRESULT - never a thread, a pointer, an IPID, an OID or an address")]
    public void TheCallEventsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(
            [
                (39, ObservationKind.RequestStart, Direction.Outbound), (40, ObservationKind.RequestEnd, Direction.Outbound),
                (43, ObservationKind.RequestStart, Direction.Inbound), (44, ObservationKind.RequestEnd, Direction.Inbound),
            ],
            plan.Events.OrderBy(descriptor => descriptor.EventId).Select(descriptor => ((int)descriptor.EventId, descriptor.Kind, descriptor.Direction)));

        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.ComCall, ObservationLayer.Application), (descriptor.Mechanism, descriptor.Layer));
            (string Field, int Offset, int Width, SourceField? Meaning)[] slots =
                [.. descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.Width, slot.SourceField)).OrderBy(slot => slot.Offset)];

            // A start keeps its trace number, the other end's process - the target at the client, the source at the server -
            // its method and, as its one identifier, its interface; the thread and causality between them are skipped.
            // A stop keeps its trace number and its HRESULT.
            (string, int, int, SourceField?)[] expected = descriptor.EventId switch
            {
                39 => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("TargetProcessId", 4, 4, SourceField.PeerProcessId),
                    ("TargetMethod", 28, 4, SourceField.ComMethodNumber), ("TargetInterface", 32, 16, null)],
                43 => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("SourceProcessId", 4, 4, SourceField.PeerProcessId),
                    ("TargetMethod", 28, 4, SourceField.ComMethodNumber), ("TargetInterface", 32, 16, null)],
                _ => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("HRESULT", 4, 4, null)],
            };
            Assert.Equal(expected, slots);
            Assert.Equal(descriptor.Kind == ObservationKind.RequestStart,
                descriptor.Slots.Any(slot => slot is { FieldName: "TargetInterface", Kind: AdmittedSlotKind.Identifier }));
            Assert.Equal(descriptor.Kind == ObservationKind.RequestEnd, descriptor.Slots.Any(slot => slot is { FieldName: "HRESULT", Role: FieldRole.Status }));

            // The process a record names is its other end's, so no slot attributes a record to a process.
            Assert.DoesNotContain(descriptor.Slots, slot => slot.Role == FieldRole.ProcessAttribution);
        }
    }

    [Fact(DisplayName = "R22: an admitted COM call record is a COM call row whose payload names no owner, so the process that raised it owns it, the process it names at its other end a source field and never its owner")]
    public void AnAdmittedRecordIsAComCallRow()
    {
        SourceAdmissionPlan plan = Compiled();
        SourceClockDescriptor clock = new(
            ClockId.Derive("com-perf-admission-tests"),
            HostId.Derive("com-perf-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        var normalizer = new ObservationNormalizerV1(clock);
        Guid activity = Guid.Parse("0c0f1b5e-3a51-4c8e-9a07-51d5ab1c2e44");

        // A client's start, raised in process 400, naming process 700 at its other end, its activity its header's.
        AdmittedEvent start = Record(plan, 39, raisedBy: 400, ("CallTraceId", 7), ("TargetProcessId", 700), ("TargetMethod", 6));
        start.SetIdentifier(Dispatch);
        start.ActivityId = activity;
        start.TimestampQpc = 5_000;
        start.RecordOrdinal = 1;
        using RecordEnvelopeV1 started = mapper.ToEnvelope(start, Plan(plan, 39), CaptureId.New());
        ObservationRowV1 row = normalizer.ToRow(started, Plan(plan, 39));
        Assert.Equal(
            (Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Outbound),
            (row.Mechanism, row.Layer, row.Kind, row.Direction));
        Assert.Equal(((int?)null, 400, QualityLevel.UnknownQuality), (row.OwnerProcessId, row.HeaderProcessId, row.AttributionQuality));
        Assert.Equal(400, RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId));
        Assert.Equal(((Guid?)Dispatch, (Guid?)activity), (row.SourceIdentifier, row.ActivityId));
        Assert.Null(row.Validate());
        Assert.Equal(
            [(SourceField.ComCallTraceId, 7L), (SourceField.PeerProcessId, 700L), (SourceField.ComMethodNumber, 6L)],
            ObservationNormalizerV1.FieldRows(started, Plan(plan, 39), row).Select(field => (field.Field, field.Value!.Value)).OrderBy(field => field.Field));
        Assert.Equal("com-call", ObservationNormalizerV1.DiscriminatorFor(Mechanism.ComCall));

        // Its stop repeats its trace number and reports the method's HRESULT as the call's status.
        AdmittedEvent stop = Record(plan, 40, raisedBy: 400, ("CallTraceId", 7), ("HRESULT", 0x8000_4002));
        stop.ActivityId = activity;
        stop.TimestampQpc = 6_000;
        stop.RecordOrdinal = 2;
        using RecordEnvelopeV1 stopped = mapper.ToEnvelope(stop, Plan(plan, 40), CaptureId.New());
        ObservationRowV1 end = normalizer.ToRow(stopped, Plan(plan, 40));
        Assert.Equal((ObservationKind.RequestEnd, (long?)0x8000_4002, FieldAvailability.Present), (end.Kind, end.StatusCode, end.StatusAvailability));
        SourceFieldRowV1 trace = Assert.Single(ObservationNormalizerV1.FieldRows(stopped, Plan(plan, 40), end));
        Assert.Equal((SourceField.ComCallTraceId, (long?)7), (trace.Field, trace.Value));
        Assert.Null(end.Validate());
    }

    [Theory(DisplayName = "R3: a COM-Perf whose schema does not admit a field com-call-link-v1 pairs, carries or links its calls by is refused, never admitted to state calls from a value never observed - the COM calls profile does not start, and icat capabilities says its schema is unknown")]
    [InlineData("ClientSyncCallStart", "CallTraceId", 39, 1)]
    [InlineData("ClientSyncCallStart", "TargetProcessId", 39, 1)]
    [InlineData("ClientSyncCallStart", "TargetMethod", 39, 1)]
    [InlineData("ClientSyncCallStop", "CallTraceId", 40, 0)]
    [InlineData("ServerSyncCallStart", "SourceProcessId", 43, 2)]
    [InlineData("ServerSyncCallStart", "TargetInterface", 43, 2)]
    [InlineData("ServerSyncCallStop", "CallTraceId", 44, 0)]
    public void AComPerfWithoutAFieldItsLinkDecidesByIsRefused(string template, string field, int eventId, int version)
    {
        // The registered layout as another build might report it, one field the link rule decides by gone from one template.
        string manifest = Without(Manifest, template, field);
        string reason = Assert.IsType<string>(RecordGates.Undecidable(AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(manifest), 0)));
        Assert.Contains($"no {field} for event {eventId} v{version}", reason, StringComparison.Ordinal);
        Assert.Contains("com-call-link-v1", reason, StringComparison.Ordinal);
        Assert.Null(RecordGates.Undecidable(Compiled()));

        // A capture's compilation refuses the source with that reason, and compiles no plan of it.
        var inventory = new CapabilityInventoryProbe(new ComPerfMetadata(manifest));
        SourcePlanCompilation compilation = inventory.CompilePlanSet([WindowsSourceCatalog.ComPerfSourceId]);
        Assert.Empty(compilation.Plans);
        SourcePlanIssue refusal = Assert.Single(compilation.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        Assert.Equal((WindowsSourceCatalog.ComPerfSourceId, reason), (refusal.SourceId, refusal.Reason));

        // So the COM calls profile does not start, COM-Perf blocking with that reason; with the whole layout it starts.
        EffectiveCapturePlan refused = ComCalls(compilation);
        Assert.False(refused.CanStart);
        ProfileSourceDecision com = Assert.Single(refused.SourceDecisions, decision => decision.SourceId == WindowsSourceCatalog.ComPerfSourceId);
        Assert.Equal((ProfileSourceDecisionState.Blocking, true, reason), (com.State, com.Required, com.Reason));
        var whole = new CapabilityInventoryProbe(new ComPerfMetadata(Manifest));
        Assert.True(ComCalls(whole.CompilePlanSet([WindowsSourceCatalog.ComPerfSourceId])).CanStart);

        // And the capability report says its schema is unknown, for that reason, where the whole layout is experimental.
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(isElevated: false);
        SourceCapability unknown = inventory.Probe(environment).Sources.Single(source => source.SourceId == WindowsSourceCatalog.ComPerfSourceId);
        Assert.Equal((CapabilityState.SchemaUnknown, reason), (unknown.State, unknown.UnavailableReason));
        Assert.Equal(CapabilityState.Experimental,
            whole.Probe(environment).Sources.Single(source => source.SourceId == WindowsSourceCatalog.ComPerfSourceId).State);
    }

    [Fact(DisplayName = "R3: a COM-Perf whose schema registers no client call stop is refused, its calls never left open at capture end for want of a stop it could not have recorded")]
    public void AComPerfWithoutAStopIsRefused()
    {
        string manifest = Manifest.Replace(
            """<event value="40" version="0" level="win:Informational" template="ClientSyncCallStop" />""", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(Manifest, manifest);
        string reason = Assert.IsType<string>(RecordGates.Undecidable(AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(manifest), 0)));
        Assert.Contains("no CallTraceId for event 40 v0 (COM client call stop), by which com-call-link-v1 joins a call's start to its stop",
            reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a COM-Perf whose schema lacks a call's HRESULT, which no rule decides by, is admitted, the call's answer then unknown")]
    public void AComPerfWithoutAnHresultIsAdmitted()
    {
        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Without(Manifest, "ClientSyncCallStop", "HRESULT")), 0);
        Assert.Null(RecordGates.Undecidable(plan));
        Assert.DoesNotContain(Plan(plan, 40).Slots, slot => slot.FieldName == "HRESULT");
        Assert.Contains(Plan(plan, 40).Slots, slot => slot.FieldName == "CallTraceId");
    }

    /// <summary>The manifest with one field of one template taken out.</summary>
    private static string Without(string manifest, string template, string field)
    {
        int start = manifest.IndexOf($"<template tid=\"{template}\">", StringComparison.Ordinal);
        int end = manifest.IndexOf("</template>", start, StringComparison.Ordinal);
        int at = manifest.IndexOf($"<data name=\"{field}\"", start, StringComparison.Ordinal);
        Assert.InRange(at, start, end);
        int close = manifest.IndexOf("/>", at, StringComparison.Ordinal) + 2;
        return manifest[..at] + manifest[close..];
    }

    /// <summary>The COM calls profile compiled with lifecycle and RPC beside COM-Perf's compilation: it asks for all three.</summary>
    private static EffectiveCapturePlan ComCalls(SourcePlanCompilation comPerf) =>
        CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.ComCalls),
            new SourcePlanCompilation(
                [
                    Fixture(WindowsSourceCatalog.KernelProcessSourceId, Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716"), 0, 1,
                        Mechanism.ProcessLifecycle, ObservationLayer.Lifecycle, ObservationKind.Create),
                    Fixture(WindowsSourceCatalog.RpcSourceId, Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39"), 1, 5,
                        Mechanism.Rpc, ObservationLayer.Application, ObservationKind.RequestStart),
                    .. comPerf.Plans.Select(plan => plan with
                    {
                        SourceIndex = 2,
                        Events = [.. plan.Events.Select(descriptor => descriptor with { SourceIndex = 2 })],
                    }),
                ],
                comPerf.Issues));

    /// <summary>A source's compiled plan of one fixture descriptor, as a machine registering it would compile.</summary>
    private static SourceAdmissionPlan Fixture(string sourceId, Guid provider, int index, int eventId, Mechanism mechanism,
        ObservationLayer layer, ObservationKind kind) => new()
    {
        SourceId = sourceId,
        ProviderGuid = provider,
        SourceIndex = index,
        Events =
        [
            new AdmittedEventPlan
            {
                SourceIndex = index,
                ProviderGuid = provider,
                EventId = eventId,
                Version = 0,
                Name = "fixture",
                Mechanism = mechanism,
                Layer = layer,
                Kind = kind,
                Direction = Direction.DirectionNotApplicable,
                MinimumBodyLength = 0,
                PointerSize = 8,
                SchemaFingerprint = "sha256:fixture-" + sourceId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                Slots = [],
                FieldReport = [],
            },
        ],
        Diagnostics = [],
    };

    /// <summary>A machine that registers COM-Perf with the layout given, and nothing else.</summary>
    private sealed class ComPerfMetadata(string manifest) : IEtwMetadataSource
    {
        private static readonly Guid Provider = Guid.Parse("b8d6861b-d20f-4eec-bbae-87e0dd80602b");

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) =>
            string.Equals(providerName, "Microsoft-Windows-COM-Perf", StringComparison.Ordinal) ? new(providerName, Provider) : null;

        public int CountPublishedProviders() => 1;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => providerGuid == Provider
            ? ManifestReadResult.Success(manifest)
            : ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) => null;
    }

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int eventId) =>
        plan.Events.Single(descriptor => descriptor.EventId == eventId);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int eventId, int raisedBy, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, eventId);
        var admitted = new AdmittedEvent
        {
            SourceIndex = descriptor.SourceIndex,
            EventId = eventId,
            Version = descriptor.Version,
            HeaderProcessId = raisedBy,
        };
        foreach ((string field, long value) in fields)
        {
            int index = descriptor.Slots.ToList().FindIndex(slot => slot.FieldName == field);
            Assert.True(index >= 0, field);
            admitted.SetSlot(index, value);
        }

        return admitted;
    }
}
