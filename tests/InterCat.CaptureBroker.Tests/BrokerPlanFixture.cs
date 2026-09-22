using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker.Tests;

internal static class BrokerPlanFixture
{
    private static readonly ProbeEnvironment Environment = new(
        "Windows fixture",
        "10.0.26200.0-x64",
        "X64",
        true,
        false,
        "local machine");

    public static BrokerRuntimeIdentity Runtime { get; } = new(
        Environment.BuildId,
        Environment.Architecture,
        CapabilityInventoryProbe.AdapterVersion);

    public static BrokerClientIdentity OwnerA { get; } = new(
        "S-1-5-21-1000",
        0x1234,
        0x2000,
        false);

    public static BrokerClientIdentity OwnerB { get; } = new(
        "S-1-5-21-2000",
        0x5678,
        0x2000,
        false);

    public static PreparedCapturePlan PreparedFocused(
        IReadOnlyList<int>? processIds = null,
        bool allowBroaderCapture = false) =>
        BrokerPrepareCompiler.Prepare(
            CompileFocused(processIds, allowBroaderCapture),
            Runtime).PreparedPlan!;

    public static EffectiveCapturePlan CompileFocused(
        IReadOnlyList<int>? processIds = null,
        bool allowBroaderCapture = false)
    {
        SourceAdmissionPlan process = BuildPlan(
            WindowsSourceCatalog.KernelProcessSourceId,
            Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716"),
            0,
            1);
        SourceAdmissionPlan network = BuildPlan(
            WindowsSourceCatalog.KernelNetworkSourceId,
            Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"),
            1,
            10,
            11);
        return CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.FocusedTransport,
                FocusedMechanism: Mechanism.Tcp,
                FocusedProcessIds: processIds,
                AllowBroaderCapture: allowBroaderCapture),
            new SourcePlanCompilation([process, network], []),
            Environment,
            new FixedTimeProvider(new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)));
    }

    private static SourceAdmissionPlan BuildPlan(
        string sourceId,
        Guid providerGuid,
        int sourceIndex,
        params int[] eventIds)
    {
        WindowsSourceDefinition definition = WindowsSourceCatalog.Find(sourceId)!;
        return new()
        {
            SourceId = sourceId,
            ProviderGuid = providerGuid,
            SourceIndex = sourceIndex,
            Events =
            [
                .. eventIds.Select((eventId, ordinal) => new AdmittedEventPlan
                {
                    SourceIndex = sourceIndex,
                    ProviderGuid = providerGuid,
                    EventId = eventId,
                    Version = 0,
                    Name = $"fixture-{eventId}",
                    Mechanism = definition.Mechanisms[0],
                    Kind = ObservationKind.Discovery,
                    Direction = Direction.DirectionNotApplicable,
                    MinimumBodyLength = 0,
                    PointerSize = 8,
                    SchemaFingerprint = "sha256:" + new string((char)('a' + ordinal), 64),
                    BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                    Slots = [],
                    FieldReport = [],
                }),
            ],
            Diagnostics = [],
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
{
    private DateTimeOffset value = value;

    public override DateTimeOffset GetUtcNow() => value;

    public void Advance(TimeSpan duration) => value = value.Add(duration);
}
