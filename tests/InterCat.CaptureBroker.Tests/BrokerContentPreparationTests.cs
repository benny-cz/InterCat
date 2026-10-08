using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// ADR-049 at prepare: the broker prepares a content capture only of its client's own processes, each read from the
/// process and named by its ID and its start, and digests what it keeps and of whom.
/// </summary>
public sealed class BrokerContentPreparationTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R22: a prepared content plan keeps one source's content under its request's limits, of processes each named by its ID and start, which its digest covers")]
    public void APreparedContentPlanNamesItsProcessesByIdAndStart()
    {
        EffectiveCapturePlan compiled = CompileContent(ContentRequest([4_243, 4_242]));
        PreparedCapturePlan plan = Prepare(compiled, [new(4_243, Started.AddSeconds(1)), new(4_242, Started)]).PreparedPlan!;

        // What it keeps, frozen: the source, its processes in order with their starts, and only that source's content.
        Assert.Equal(WindowsSourceCatalog.WinInetCaptureSourceId, plan.Content!.SourceId);
        Assert.Equal([new(4_242, Started), new(4_243, Started.AddSeconds(1))], plan.ContentProcesses.ToArray());
        Assert.Equal((4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText),
            (plan.Content.MaximumRecordBytes, plan.Content.MaximumSessionBytes, plan.Content.Inspection));
        Assert.All(plan.Sources, source => Assert.All(source.Events, item => Assert.Equal(
            source.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId
                ? CaptureBodyAdmissionPolicies.ScopedContentRequestPolicyId
                : CaptureBodyAdmissionPolicies.MetadataOnlyPolicyId,
            item.BodyPolicy.PolicyId)));
        Assert.Equal([4_242, 4_243], plan.Providers.Single(provider => provider.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId)
            .ProcessIdsToInclude);

        // The digest names the processes by their starts, and the policy's limits and consent, so each changes it.
        string digest = plan.Digest;
        Assert.Equal(digest, Prepare(compiled, [new(4_242, Started), new(4_243, Started.AddSeconds(1))]).PreparedPlan!.Digest);
        Assert.NotEqual(digest, Prepare(compiled, [new(4_242, Started.AddTicks(1)), new(4_243, Started.AddSeconds(1))])
            .PreparedPlan!.Digest);
        foreach (ContentCaptureRequest changed in new[]
        {
            ContentRequest([4_243, 4_242]) with { MaximumRecordBytes = 8_192 },
            ContentRequest([4_243, 4_242]) with { MaximumSessionBytes = 32L * 1024 * 1024 },
            ContentRequest([4_243, 4_242]) with { Inspection = ContentInspectionMode.Disabled },
        })
        {
            Assert.NotEqual(digest, Prepare(CompileContent(changed), [new(4_242, Started), new(4_243, Started.AddSeconds(1))])
                .PreparedPlan!.Digest);
        }
    }

    [Fact(DisplayName = "§9.2: a plan that keeps no content keeps the digest it had before content plans were digested")]
    public void APlanKeepingNoContentKeepsItsDigest()
    {
        // Digested before the content section existed; only a change to what such a plan is - its adapter's version among
        // it - may move these.
        Assert.Equal("sha256:3d33d1dfbd8e009d0fc9374c8126508724eba26c3f101123205c46523586df91", PreparedFocused().Digest);
        Assert.Equal("sha256:e40cd33b26bb87f446cd082f57b7b68d55baaa10ec470aa7f2a20c96a3c58ba7",
            PreparedFocused([84], allowBroaderCapture: true).Digest);
    }

    [Fact(DisplayName = "R22: a content plan is refused without each of its processes named by its start, and a plan keeping no content is refused one naming processes")]
    public void AContentPlanNeedsItsProcessesNamed()
    {
        EffectiveCapturePlan compiled = CompileContent(ContentRequest([4_243, 4_242]));
        foreach (IReadOnlyList<BrokerNamedProcess>? named in new IReadOnlyList<BrokerNamedProcess>?[]
        {
            null,
            [new(4_242, Started)],
            [new(4_242, Started), new(4_244, Started)],
            [new(4_242, Started), new(4_243, default)],
        })
        {
            BrokerPrepareResult refused = Prepare(compiled, named);
            Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, refused.Refusal!.Code);
            Assert.Equal("A content plan names each of its processes by its ID and its start, as the broker read them.",
                refused.Refusal.Message);
        }

        BrokerPrepareResult focused = Prepare(CompileFocused(), [new(4_242, Started)]);
        Assert.Equal(("Processes were named for content, but the plan keeps none.", BrokerPrepareRefusalCode.InvalidCapturePlan),
            (focused.Refusal!.Message, focused.Refusal.Code));
    }

    [Fact(DisplayName = "R16: a content plan keeps content only of the source its policy names, from one slot a descriptor sizes, and its other sources keep metadata only")]
    public void ContentIsKeptOnlyWhereThePolicyNamesIt()
    {
        EffectiveCapturePlan compiled = CompileContent();
        IReadOnlyList<BrokerNamedProcess> named = [new(4_242, Started)];
        Assert.True(Prepare(compiled, named).IsPrepared);

        // Lifecycle descriptors carrying the content policy would keep what the request never named.
        EffectiveCapturePlan lifecycleKeeps = compiled with
        {
            Sources =
            [
                .. compiled.Sources.Select(source => source.SourceId == WindowsSourceCatalog.KernelProcessSourceId
                    ? source with { Events = [.. source.Events.Select(item => item with { BodyPolicy = compiled.BodyPolicy! })] }
                    : source),
            ],
        };
        Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, Prepare(lifecycleKeeps, named).Refusal!.Code);

        // A content slot whose length field does not come before it, or a second one, is refused.
        foreach (Func<AdmittedSlotPlan, IEnumerable<AdmittedSlotPlan>> change in new Func<AdmittedSlotPlan, IEnumerable<AdmittedSlotPlan>>[]
        {
            slot => slot.Kind == AdmittedSlotKind.Content ? [slot with { LengthOffset = slot.Offset }] : [slot],
            slot => slot.Kind == AdmittedSlotKind.Content ? [slot with { LengthWidth = 3 }] : [slot],
            slot => slot.Kind == AdmittedSlotKind.Content ? [slot with { ContentEncoding = null }] : [slot],
            slot => slot.Kind == AdmittedSlotKind.Content ? [slot, slot with { FieldName = "Again" }] : [slot],
        })
        {
            EffectiveCapturePlan changed = compiled with
            {
                Sources =
                [
                    .. compiled.Sources.Select(source => source with
                    {
                        Events = [.. source.Events.Select(item => item with { Slots = [.. item.Slots.SelectMany(change)] })],
                    }),
                ],
            };
            Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, Prepare(changed, named).Refusal!.Code);
        }

        // A content slot on a source whose content the policy does not keep is refused, as one on lifecycle would be.
        AdmittedSlotPlan contentSlot = compiled.Sources.Single(source => source.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId)
            .Events[0].Slots.Single(slot => slot.Kind == AdmittedSlotKind.Content);
        EffectiveCapturePlan lifecycleSlot = compiled with
        {
            Sources =
            [
                .. compiled.Sources.Select(source => source.SourceId == WindowsSourceCatalog.KernelProcessSourceId
                    ? source with
                    {
                        Events = [.. source.Events.Select(item => item with
                        {
                            MinimumBodyLength = contentSlot.Offset,
                            Slots = [.. item.Slots, contentSlot],
                        })],
                    }
                    : source),
            ],
        };
        Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, Prepare(lifecycleSlot, named).Refusal!.Code);

        // Its scope is its named processes' content and the lifecycle they rest on, with no broader capture to consent to.
        foreach (CaptureScopeDecision scope in new[]
        {
            compiled.Scope with { BroaderCaptureNeedsConsent = true, BroaderCaptureAccepted = true },
            compiled.Scope with
            {
                Sources = [.. compiled.Scope.Sources.Select(source => source with { ProcessScope = ProviderProcessScope.WholeMachineRequiredContext })],
            },
            compiled.Scope with { RequestedProcessIds = [4_242, 4_243], InitialViewProcessIds = [4_242, 4_243] },
        })
        {
            Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, Prepare(compiled with { Scope = scope }, named).Refusal!.Code);
        }

        // Limits or consent the policy and the request disagree on are refused rather than trusted.
        Assert.Equal(BrokerPrepareRefusalCode.InvalidCapturePlan, Prepare(compiled with
        {
            Content = compiled.Content! with { MaximumSessionBytes = compiled.Content.MaximumSessionBytes * 2 },
        }, named).Refusal!.Code);
    }

    [Fact(DisplayName = "P19: the broker prepares a content capture only of its client's own processes, read from each, says when one is not, and states each one's start for the review")]
    public async Task TheBrokerPreparesOnlyItsClientsOwnProcesses()
    {
        var reader = new FakeProcessReader { [4_242] = new(OwnerA, Started) };
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(new ContentPlanSource(), registry, Runtime, reader);
        BrokerPrepareCaptureRequest request = new(
            "content", null, [], false, false, Quota, BrokerRetentionPolicy.StopAtLimit, ContentRequest());

        // The client's own process: prepared, its start stated beside it for the review.
        BrokerPrepareCaptureResponse prepared = await preparation.PrepareAsync(request, OwnerA);
        Assert.True(prepared.Prepared);
        Assert.Equal([4_242], prepared.Summary.RequestedProcessIds);
        BrokerEffectiveContentSummary content = prepared.Summary.Content!;
        Assert.Equal((WindowsSourceCatalog.WinInetCaptureSourceId, 4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText),
            (content.SourceId, content.MaximumRecordBytes, content.MaximumSessionBytes, content.Inspection));
        Assert.Equal([Started], content.ProcessStartsUtc);
        Assert.Equal(["*"], content.ChannelSelectors);
        Assert.Equal(Prepare(CompileContent(), [new(4_242, Started)]).PreparedPlan!.Digest, prepared.Grant!.PlanDigest);

        // Another user's process is refused before anything is prepared, saying whose it is not.
        reader[4_242] = new(OwnerB, Started);
        BrokerPrepareCaptureResponse refused = await preparation.PrepareAsync(request, OwnerA);
        Assert.Equal((false, BrokerPrepareRefusalCode.ContentProcessRefused, (PreparedPlanGrant?)null),
            (refused.Prepared, refused.RefusalCode, refused.Grant));
        Assert.Equal("Process 4242 runs as another user. Content is kept only from your own processes.", refused.RefusalReason);
        Assert.Empty(refused.Summary.Content!.ProcessStartsUtc);

        // A broker that cannot read processes keeps no content.
        using var blind = new BrokerPreparationCoordinator(new ContentPlanSource(), new(), Runtime);
        BrokerPrepareCaptureResponse unread = await blind.PrepareAsync(request, OwnerA);
        Assert.Equal((false, BrokerPrepareRefusalCode.ContentProcessRefused), (unread.Prepared, unread.RefusalCode));
        Assert.Equal("This broker cannot read the processes a content request names, so it keeps no content.", unread.RefusalReason);
    }

    [Fact(DisplayName = "R16: a prepared content capture is not started until the broker holds its processes, saying so, and nothing of it is kept")]
    public async Task AContentCaptureIsNotStartedYet()
    {
        var registry = new PreparedPlanRegistry();
        PreparedPlanGrant grant = registry.Issue(Prepare(CompileContent(), [new(4_242, Started)]).PreparedPlan!, OwnerA);
        var runtime = new BrokerFakeRuntime();
        var store = new InMemoryBrokerLifecycleStore();
        using var lifecycle = new BrokerLifecycleCoordinator(registry, store, runtime);
        BrokerStartOutcome outcome = await lifecycle.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        Assert.Equal((BrokerOperationCode.StartFailed, (CaptureId?)null, CaptureLifecycle.Idle),
            (outcome.Code, outcome.CaptureId, outcome.State));
        Assert.Equal("A content capture is prepared for review only: this broker does not start one yet.", outcome.FailureReason);
        Assert.Empty(runtime.StartedPlans);
    }

    private static BrokerPrepareResult Prepare(EffectiveCapturePlan plan, IReadOnlyList<BrokerNamedProcess>? named) =>
        BrokerPrepareCompiler.Prepare(plan, Quota, BrokerRetentionPolicy.StopAtLimit, Runtime, contentProcesses: named);

    /// <summary>Compiles a content request's plan, as the broker's own compiler does from the machine's schemas.</summary>
    private sealed class ContentPlanSource : IBrokerCapturePlanSource
    {
        public ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A content preparation test probes nothing.");

        public ValueTask<EffectiveCapturePlan> CompileAsync(CaptureProfileRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CompileContent(request.Content));
    }

    /// <summary>Processes by ID, as a test sets them; any other ID is not running.</summary>
    private sealed class FakeProcessReader : Dictionary<int, BrokerProcessReading>, IBrokerProcessReader
    {
        public BrokerProcessReading? Read(int processId, out string? problem)
        {
            problem = TryGetValue(processId, out BrokerProcessReading? reading)
                ? null
                : $"process {processId} is not running, so its ID could be given to any process";
            return reading;
        }
    }
}
