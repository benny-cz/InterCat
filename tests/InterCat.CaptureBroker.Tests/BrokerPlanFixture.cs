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

    /// <summary>A content request for WinINet's HTTP messages of the named processes, within 4 KiB a record and 16 MiB in all.</summary>
    public static ContentCaptureRequest ContentRequest(IReadOnlyList<int>? processIds = null) => new()
    {
        SourceId = WindowsSourceCatalog.WinInetCaptureSourceId,
        Mechanism = Mechanism.Http,
        ProcessIds = processIds ?? [4_242],
        ChannelSelectors = [ContentCapturePolicyCompiler.EveryChannel],
        MaximumRecordBytes = 4_096,
        MaximumSessionBytes = 16L * 1024 * 1024,
        Retention = ContentRetentionMode.StopAtLimit,
        Inspection = ContentInspectionMode.HexAndText,
    };

    /// <summary>
    /// A content plan of WinINet's capture provider, compiled from the layout its registration gives (ADR-037), beside the
    /// lifecycle its processes' identity rests on.
    /// </summary>
    public static EffectiveCapturePlan CompileContent(ContentCaptureRequest? request = null)
    {
        request ??= ContentRequest();
        CompiledBodyAdmissionPolicy policy = CaptureBodyAdmissionPolicies.ScopedContentRequest(ContentCapturePolicyCompiler.Compile(request));
        SourceAdmissionPlan process = BuildPlan(
            WindowsSourceCatalog.KernelProcessSourceId,
            Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716"),
            0,
            1);
        SourceAdmissionPlan http = AdmissionPlanCompiler.Compile(
            WindowsSourceCatalog.Find(WindowsSourceCatalog.WinInetCaptureSourceId)!,
            ManifestParser.Parse(WinInetManifest),
            1,
            bodyPolicy: policy);
        return CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Content, Content: request),
            new SourcePlanCompilation([process, http], []),
            Environment,
            new FixedTimeProvider(new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)));
    }

    // WinINet's capture provider as TDH gave its layout on Windows 10.0.26220 (ADR-037).
    private const string WinInetManifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-WinINet-Capture" guid="{a70ff94f-570b-4979-ba5c-e59c9feab61b}">
           <templates>
            <template tid="Capture">
             <data name="SessionId" inType="win:UInt32" />
             <data name="SequenceNumber" inType="win:UInt32" />
             <data name="Flags" inType="win:UInt32" />
             <data name="PayloadByteLength" inType="win:UInt32" />
             <data name="Payload" inType="win:Binary" length="PayloadByteLength" />
            </template>
           </templates>
           <events>
            <event value="2001" version="0" level="win:Informational" template="Capture" />
            <event value="2002" version="0" level="win:Informational" template="Capture" />
            <event value="2003" version="0" level="win:Informational" template="Capture" />
            <event value="2004" version="0" level="win:Informational" template="Capture" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

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

internal sealed class BrokerFakeRuntime : IBrokerCaptureRuntime, IBrokerCaptureCompletionProbe, IBrokerFollowRelease
{
    /// <summary>What each renewal said its capture's follow gave up, in order.</summary>
    public List<(CaptureId Capture, long Chunks)> FollowReleases { get; } = [];

    public void FollowReleased(CaptureId captureId, long chunks) => FollowReleases.Add((captureId, chunks));

    public BrokerRuntimeStartOutcome StartOutcome { get; init; } = new(true);
    public BrokerRuntimeStopOutcome StopOutcome { get; init; } = new(new(true, true, true, true, true));
    public Func<CaptureId, Task>? BeforeStart { get; set; }
    public Queue<BrokerRuntimeStopOutcome> StopOutcomes { get; } = [];
    public HashSet<CaptureId> CompletedCaptures { get; } = [];
    public List<BrokerSessionOwnership> StoppedSessions { get; } = [];
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    /// <summary>The client PID each start was given, in order.</summary>
    public List<int?> StartedClients { get; } = [];

    /// <summary>The prepared plan each start was given, in order.</summary>
    public List<PreparedCapturePlan> StartedPlans { get; } = [];

    /// <summary>The authenticated client each start was given, in order.</summary>
    public List<BrokerClientIdentity?> StartedIdentities { get; } = [];

    public ValueTask<bool> HasCompletedAsync(CaptureId captureId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CompletedCaptures.Contains(captureId));
    }

    public async Task<BrokerRuntimeStartOutcome> StartAsync(
        BrokerCaptureOwnership ownership,
        PreparedCapturePlan plan,
        int? clientProcessId,
        CancellationToken cancellationToken,
        BrokerClientIdentity? client = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ownership.Session.IsValidFor(ownership.CaptureId))
        {
            throw new InvalidOperationException("The fixture received invalid session ownership.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        StartedClients.Add(clientProcessId);
        StartedPlans.Add(plan);
        StartedIdentities.Add(client);
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

/// <summary>
/// Processes by ID, as a test sets them; any other ID is not running. What it holds stays held until disposed, read as
/// the processes were when held, and a test can see what is held still.
/// </summary>
internal sealed class FakeProcessReader : Dictionary<int, BrokerProcessReading>, IBrokerProcessReader
{
    /// <summary>The process IDs held now, across every hold not yet disposed.</summary>
    public List<int> Held { get; } = [];

    public BrokerProcessReading? Read(int processId, out string? problem)
    {
        problem = TryGetValue(processId, out BrokerProcessReading? reading)
            ? null
            : $"process {processId} is not running, so its ID could be given to any process";
        return reading;
    }

    public IBrokerHeldProcesses? Hold(IReadOnlyList<int> processIds, out string? problem)
    {
        var readings = new Dictionary<int, BrokerProcessReading>();
        foreach (int processId in processIds)
        {
            if (Read(processId, out problem) is not { } reading)
            {
                return null;
            }

            readings[processId] = reading;
        }

        problem = null;
        Held.AddRange(processIds);
        return new Holding(this, [.. processIds], readings);
    }

    private sealed class Holding(FakeProcessReader reader, int[] processIds, IReadOnlyDictionary<int, BrokerProcessReading> readings)
        : IBrokerHeldProcesses
    {
        private bool disposed;

        public IReadOnlyDictionary<int, BrokerProcessReading> Readings { get; } = readings;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                foreach (int processId in processIds)
                {
                    _ = reader.Held.Remove(processId);
                }
            }
        }
    }
}
