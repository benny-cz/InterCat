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

    public static BrokerCaptureQuota Quota { get; } = new(
        MaximumDurationSeconds: 1_800,
        MaximumJournalBytes: 4L * 1024 * 1024 * 1024,
        MinimumFreeDiskBytes: 512L * 1024 * 1024);

    public static PreparedCapturePlan PreparedFocused(
        IReadOnlyList<int>? processIds = null,
        bool allowBroaderCapture = false) =>
        BrokerPrepareCompiler.Prepare(
            CompileFocused(processIds, allowBroaderCapture),
            Quota,
            BrokerRetentionPolicy.StopAtLimit,
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
                    Layer = ObservationLayer.Transport,
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

internal sealed class BrokerFakeRuntime : IBrokerCaptureRuntime, IBrokerCaptureCompletionProbe
{
    public BrokerRuntimeStartOutcome StartOutcome { get; init; } = new(true);
    public BrokerRuntimeStopOutcome StopOutcome { get; init; } = new(new(true, true, true, true, true));
    public Func<CaptureId, Task>? BeforeStart { get; set; }
    public Queue<BrokerRuntimeStopOutcome> StopOutcomes { get; } = [];
    public HashSet<CaptureId> CompletedCaptures { get; } = [];
    public List<BrokerSessionOwnership> StoppedSessions { get; } = [];
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public ValueTask<bool> HasCompletedAsync(CaptureId captureId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CompletedCaptures.Contains(captureId));
    }

    public async Task<BrokerRuntimeStartOutcome> StartAsync(
        BrokerCaptureOwnership ownership,
        PreparedCapturePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ownership.Session.IsValidFor(ownership.CaptureId))
        {
            throw new InvalidOperationException("The fixture received invalid session ownership.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        if (BeforeStart is not null)
        {
            await BeforeStart(ownership.CaptureId);
        }

        return StartOutcome;
    }

    public Task<BrokerRuntimeStopOutcome> StopAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken)
    {
        if (!ownership.Session.IsValidFor(ownership.CaptureId))
        {
            throw new InvalidOperationException("The fixture received invalid capture ownership.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        CompletedCaptures.Remove(ownership.CaptureId);
        StoppedSessions.Add(ownership.Session);
        return Task.FromResult(StopOutcomes.Count > 0 ? StopOutcomes.Dequeue() : StopOutcome);
    }
}
