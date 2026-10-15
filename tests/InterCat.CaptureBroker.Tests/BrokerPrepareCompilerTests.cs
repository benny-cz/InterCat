using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerPrepareCompilerTests
{

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

        BrokerPrepareResult first = Prepare(mutable);
        BrokerPrepareResult second = Prepare(mutable);

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

        string left = Prepare(plan).PreparedPlan!.Digest;
        string right = Prepare(reordered).PreparedPlan!.Digest;

        Assert.Equal(left, right);
    }

    [Fact]
    public void DifferentReviewedProcessFocusChangesDigest()
    {
        PreparedCapturePlan first = BrokerPrepareCompiler
            .Prepare(CompileFocused([84], allowBroaderCapture: true), Quota, BrokerRetentionPolicy.StopAtLimit, Runtime)
            .PreparedPlan!;
        PreparedCapturePlan second = BrokerPrepareCompiler
            .Prepare(CompileFocused([85], allowBroaderCapture: true), Quota, BrokerRetentionPolicy.StopAtLimit, Runtime)
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

        BrokerPrepareResult result = Prepare(CompileFocused(), runtime);

        Assert.False(result.IsPrepared);
        Assert.Null(result.PreparedPlan);
        Assert.Equal(expected, result.Refusal!.Code);
    }

    [Fact]
    public void BlockedScopeConsentCannotBePrepared()
    {
        EffectiveCapturePlan blocked = CompileFocused([84], allowBroaderCapture: false);

        BrokerPrepareResult result = Prepare(blocked);

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

        BrokerPrepareResult result = Prepare(forgedPlan);

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

        BrokerPrepareResult result = Prepare(forged);

        Assert.False(result.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, result.Refusal!.Code);
        Assert.Contains("allowlist", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewedOperationalLimitsAreFrozenAndChangeDigest()
    {
        EffectiveCapturePlan plan = CompileFocused();
        PreparedCapturePlan first = Prepare(plan).PreparedPlan!;
        var shorter = Quota with { MaximumDurationSeconds = Quota.MaximumDurationSeconds - 1 };
        PreparedCapturePlan second = BrokerPrepareCompiler
            .Prepare(plan, shorter, BrokerRetentionPolicy.StopAtLimit, Runtime)
            .PreparedPlan!;

        Assert.Equal(Quota, first.Quota);
        Assert.Equal(BrokerRetentionPolicy.StopAtLimit, first.Retention);
        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Fact]
    public void InvalidOperationalLimitsCannotBePrepared()
    {
        var invalid = Quota with { MaximumDurationSeconds = 0 };

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(
            CompileFocused(),
            invalid,
            BrokerRetentionPolicy.StopAtLimit,
            Runtime);

        Assert.False(result.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.InvalidOperationalLimits, result.Refusal!.Code);
    }

    [Fact]
    public void JournalPublicationIsFrozenIntoThePlanAndItsDigest()
    {
        EffectiveCapturePlan plan = CompileFocused();

        PreparedCapturePlan onStop = BrokerPrepareCompiler.Prepare(
            plan, Quota, BrokerRetentionPolicy.StopAtLimit, Runtime).PreparedPlan!;
        PreparedCapturePlan live = BrokerPrepareCompiler.Prepare(
            plan, Quota, BrokerRetentionPolicy.StopAtLimit, Runtime, BrokerJournalPublication.Live).PreparedPlan!;
        BrokerPrepareResult unknown = BrokerPrepareCompiler.Prepare(
            plan, Quota, BrokerRetentionPolicy.StopAtLimit, Runtime, (BrokerJournalPublication)9);

        Assert.Null(onStop.PublicationInterval);
        Assert.Null(onStop.FirstPublication);
        Assert.Equal(BrokerJournalPublicationPolicy.FirstLivePublication, live.FirstPublication);
        Assert.Equal(BrokerJournalPublication.Live, live.Publication);
        Assert.Equal(
            BrokerJournalPublicationPolicy.Interval(BrokerJournalPublication.Live, Quota.MaximumDurationSeconds),
            live.PublicationInterval);
        Assert.NotEqual(onStop.Digest, live.Digest);
        Assert.False(unknown.IsPrepared);
        Assert.Equal(BrokerPrepareRefusalCode.InvalidOperationalLimits, unknown.Refusal!.Code);
    }

    [Fact]
    public void DiskReserveMayExceedJournalAllowance()
    {
        var limits = new BrokerCaptureQuota(30, 1_048_576, 16_777_216);

        BrokerPrepareResult result = BrokerPrepareCompiler.Prepare(
            CompileFocused(), limits, BrokerRetentionPolicy.StopAtLimit, Runtime);

        Assert.True(result.IsPrepared, result.Refusal?.Message);
        Assert.Equal(limits, result.PreparedPlan!.Quota);
    }

    [Fact(DisplayName = "§9.4: the broker prepares RPC peers as the catalog states it, ALPC enabled as kernel flags rather than a provider request")]
    public void RpcPeersIsPrepared()
    {
        BrokerPrepareResult result = Prepare(CompileRpcPeers());
        Assert.True(result.IsPrepared, result.Refusal?.Message);
        PreparedCapturePlan prepared = result.PreparedPlan!;
        Assert.Equal(("rpc-peers", "rpc-peers"), (prepared.RequestedProfileId, prepared.EffectiveProfileId));
        Assert.Equal([WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.RpcSourceId],
            prepared.Providers.Select(provider => provider.SourceId));
        Assert.Contains(prepared.Sources, source => source.SourceId == WindowsSourceCatalog.KernelAlpcSourceId);

        // The one session it starts is a private system logger: ALPC's kernel flags, its first enablement (ADR-035).
        var session = new OwnedSessionPlan
        {
            Identity = CaptureSessionIdentity.Create("rpc-peers", 4242),
            Providers = prepared.Providers,
            Sources = prepared.Sources,
        };
        Assert.NotEqual(0UL, session.KernelFlags);
        Assert.Equal(WindowsSourceCatalog.KernelAlpcSourceId, session.KernelFlagSources);
    }

    [Fact(DisplayName = "§9.4: the broker refuses an RPC peers plan that asks a provider request for ALPC, or filters ALPC by process")]
    public void AKernelFlagGroupTakesNoProviderRequestOrFilter()
    {
        EffectiveCapturePlan plan = CompileRpcPeers();
        ProviderEnablementRequest lifecycle = plan.Providers[0];
        const string KernelFlagGroup = "is a kernel flag group, which no provider request enables or filters";

        // A provider request for ALPC is no allowlisted enablement of it: its flags enable it, first, and nothing else does.
        // Beside the requests the plan has, it is one request too many; in place of RPC's, it is still refused as itself.
        BrokerPrepareResult requested = Prepare(plan with
        {
            Providers = [.. plan.Providers, lifecycle with { SourceId = WindowsSourceCatalog.KernelAlpcSourceId }],
        });
        Assert.False(requested.IsPrepared);
        Assert.Contains("exactly one provider request per source a provider is enabled for", requested.Refusal!.Message,
            StringComparison.Ordinal);
        BrokerPrepareResult replaced = Prepare(plan with
        {
            Providers =
            [
                .. plan.Providers.Select(provider => provider.SourceId == WindowsSourceCatalog.RpcSourceId
                    ? lifecycle with { SourceId = WindowsSourceCatalog.KernelAlpcSourceId }
                    : provider),
            ],
        });
        Assert.False(replaced.IsPrepared);
        Assert.Equal($"Source '{WindowsSourceCatalog.KernelAlpcSourceId}' {KernelFlagGroup}.", replaced.Refusal!.Message);

        // Nor can ALPC be filtered to processes, or said to be scoped as no machine-wide source is: no provider request
        // carries a filter to it, and its flags reach the whole machine.
        foreach ((string name, Func<ProviderScopeDecision, ProviderScopeDecision> alter, bool outside) in
            new (string, Func<ProviderScopeDecision, ProviderScopeDecision>, bool)[]
            {
                ("filtered", source => source with { ProcessScope = ProviderProcessScope.ProcessFiltered }, false),
                ("an undefined scope", source => source with { ProcessScope = (ProviderProcessScope)99 }, false),
                ("process IDs applied", source => source with { AppliedProcessIds = [4242] }, false),
                ("outside processes no one asked for", source => source with { CapturesOutsideRequestedProcesses = true },
                    true),
            })
        {
            BrokerPrepareResult refused = Prepare(plan with
            {
                Scope = plan.Scope with
                {
                    CapturesOutsideRequestedProcesses = outside,
                    Sources =
                    [
                        .. plan.Scope.Sources.Select(source => source.SourceId == WindowsSourceCatalog.KernelAlpcSourceId
                            ? alter(source)
                            : source),
                    ],
                },
            });
            Assert.False(refused.IsPrepared, name);
            Assert.Equal($"Source '{WindowsSourceCatalog.KernelAlpcSourceId}' {KernelFlagGroup}.", refused.Refusal!.Message);
        }
    }

    private static BrokerPrepareResult Prepare(
        EffectiveCapturePlan plan,
        BrokerRuntimeIdentity? runtime = null) =>
        BrokerPrepareCompiler.Prepare(
            plan,
            Quota,
            BrokerRetentionPolicy.StopAtLimit,
            runtime ?? Runtime);

}
