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

    private static BrokerPrepareResult Prepare(
        EffectiveCapturePlan plan,
        BrokerRuntimeIdentity? runtime = null) =>
        BrokerPrepareCompiler.Prepare(
            plan,
            Quota,
            BrokerRetentionPolicy.StopAtLimit,
            runtime ?? Runtime);

}
