using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerPrepareCompilerTests
{
    private static readonly ProbeEnvironment Environment = new(
        "Windows fixture",
        "10.0.26200.0-x64",
        "X64",
        true,
        false,
        "local machine");

    private static readonly BrokerRuntimeIdentity Runtime = new(
        Environment.BuildId,
        Environment.Architecture,
        CapabilityInventoryProbe.AdapterVersion);

    [Fact]
    public void StartablePlanProducesDeepFrozenDeterministicHandoff()
    {
        EffectiveCapturePlan source = CompileFocused([84], allowBroaderCapture: true);
        var mutableEventIds = new List<int>(source.Providers[1].EventIdsToEnable);
        EffectiveCapturePlan mutable = source with
        {
            Providers =
            [
                source.Providers[0],
                source.Providers[1] with { EventIdsToEnable = mutableEventIds },
            ],
        };

        BrokerPrepareResult first = BrokerPrepareCompiler.Prepare(mutable, Runtime);
        BrokerPrepareResult second = BrokerPrepareCompiler.Prepare(mutable, Runtime);

        Assert.True(first.IsPrepared);
        Assert.Null(first.Refusal);
        PreparedCapturePlan prepared = Assert.IsType<PreparedCapturePlan>(first.PreparedPlan);
        Assert.Equal(BrokerProtocol.Version, prepared.ProtocolVersion);
        Assert.StartsWith("sha256:", prepared.Digest, StringComparison.Ordinal);
        Assert.Equal(71, prepared.Digest.Length);
        Assert.Equal(prepared.Digest, second.PreparedPlan!.Digest);
        Assert.Equal([84], prepared.Scope.InitialViewProcessIds);

        mutableEventIds.Add(999);

        Assert.DoesNotContain(999, prepared.Providers.SelectMany(provider => provider.EventIdsToEnable));
        Assert.Equal(prepared.Digest, first.PreparedPlan.Digest);
    }

    [Fact]
    public void CanonicalOrderingDoesNotChangeDigest()
    {
        EffectiveCapturePlan plan = CompileFocused([84, 4242], allowBroaderCapture: true);
        EffectiveCapturePlan reordered = plan with
        {
            Sources =
            [
                .. plan.Sources
                    .Reverse()
                    .Select(source => source with { Events = [.. source.Events.Reverse()] }),
            ],
            Providers =
            [
                .. plan.Providers
                    .Reverse()
                    .Select(provider => provider with
                    {
                        EventIdsToEnable = [.. provider.EventIdsToEnable.Reverse()],
                        EventIdsToDisable = [.. provider.EventIdsToDisable.Reverse()],
                    }),
            ],
            Scope = plan.Scope with { Sources = [.. plan.Scope.Sources.Reverse()] },
        };

        string left = BrokerPrepareCompiler.Prepare(plan, Runtime).PreparedPlan!.Digest;
        string right = BrokerPrepareCompiler.Prepare(reordered, Runtime).PreparedPlan!.Digest;

        Assert.Equal(left, right);
    }

    [Fact]
    public void DifferentReviewedProcessFocusChangesDigest()
    {
        PreparedCapturePlan first = BrokerPrepareCompiler
            .Prepare(CompileFocused([84], allowBroaderCapture: true), Runtime)
            .PreparedPlan!;
        PreparedCapturePlan second = BrokerPrepareCompiler
            .Prepare(CompileFocused([85], allowBroaderCapture: true), Runtime)
            .PreparedPlan!;

        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Theory]
    [InlineData("build", BrokerPrepareRefusalCode.RuntimeBuildMismatch)]
    [InlineData("architecture", BrokerPrepareRefusalCode.RuntimeArchitectureMismatch)]
    [InlineData("adapter", BrokerPrepareRefusalCode.RuntimeAdapterMismatch)]
    public void RuntimeMismatchIsTypedAndProducesNoPlan(
        string mismatch,
        BrokerPrepareRefusalCode expected)
    {
        BrokerRuntimeIdentity runtime = mismatch switch
        {
            "build" => Runtime with { BuildId = "different-build" },
            "architecture" => Runtime with { Architecture = "ARM64" },
            _ => Runtime with { AdapterVersion = "different-adapter" },
        };

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(CompileFocused(), runtime);

        Assert.False(result.IsPrepared);
        Assert.Null(result.PreparedPlan);
        Assert.Equal(expected, result.Refusal!.Code);
    }

    [Fact]
    public void BlockedScopeConsentCannotBePrepared()
    {
        EffectiveCapturePlan blocked = CompileFocused([84], allowBroaderCapture: false);

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(blocked, Runtime);

        Assert.False(result.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.PlanNotStartable, result.Refusal!.Code);
    }

    [Fact]
    public void ForgedAdmissionPolicyCannotBePrepared()
    {
        EffectiveCapturePlan plan = CompileFocused();
        CompiledBodyAdmissionPolicy forged = plan.BodyPolicy! with
        {
            RetainsOriginalSourceBytes = true,
        };
        EffectiveCapturePlan forgedPlan = plan with
        {
            BodyPolicy = forged,
            Sources =
            [
                .. plan.Sources.Select(source => source with
                {
                    Events =
                    [
                        .. source.Events.Select(item => item with { BodyPolicy = forged }),
                    ],
                }),
            ],
        };

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(forgedPlan, Runtime);

        Assert.False(result.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.UnsupportedAdmissionPolicy, result.Refusal!.Code);
    }

    [Fact]
    public void ProviderRequestOutsideAdapterAllowlistCannotBePrepared()
    {
        EffectiveCapturePlan plan = CompileFocused();
        EffectiveCapturePlan forged = plan with
        {
            Providers =
            [
                plan.Providers[0],
                plan.Providers[1] with { MatchAnyKeyword = ulong.MaxValue },
            ],
        };

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(forged, Runtime);

        Assert.False(result.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, result.Refusal!.Code);
        Assert.Contains("allowlist", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EffectiveCapturePlan CompileFocused(
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
