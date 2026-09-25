using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerConnectionDispatcherTests
{
    [Fact]
    public async Task CommandBeforeHelloIsRejectedWithoutCallingServices()
    {
        var source = new FakePlanSource();
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        Guid correlationId = Guid.NewGuid();

        BrokerWireFrame response = await dispatcher.DispatchAsync(
            BrokerWireRequestCodec.Encode(new BrokerGetCapabilitiesRequest(), correlationId));
        var error = Assert.IsType<BrokerErrorResponse>(BrokerWireResponseCodec.Decode(response));

        Assert.Equal(BrokerErrorCode.HelloRequired, error.Code);
        Assert.Equal(correlationId, response.CorrelationId);
        Assert.Equal(0, source.ProbeCount);
    }

    [Fact]
    public async Task HelloThenCapabilitiesPreservesCorrelationAndReturnsReviewedCatalog()
    {
        var source = new FakePlanSource();
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(dispatcher);
        Guid correlationId = Guid.NewGuid();

        BrokerWireFrame response = await dispatcher.DispatchAsync(
            BrokerWireRequestCodec.Encode(new BrokerGetCapabilitiesRequest(), correlationId));
        var capabilities = Assert.IsType<BrokerCapabilitiesResponse>(BrokerWireResponseCodec.Decode(response));

        Assert.Equal(correlationId, response.CorrelationId);
        Assert.Contains(capabilities.Profiles, profile => profile.ProfileId == "explore");
        Assert.Contains(capabilities.Mechanisms, mechanism => mechanism.Mechanism == Mechanism.Tcp);
        Assert.Equal(1, source.ProbeCount);
    }

    [Fact]
    public async Task RejectedHelloCanBeCorrectedButSuccessfulHelloCannotBeRenegotiated()
    {
        var source = new FakePlanSource();
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        var futureRequired = (BrokerProtocolFeature)(1UL << 63);
        var rejected = Assert.IsType<BrokerErrorResponse>(await Dispatch(
            dispatcher,
            new BrokerHelloRequest(Guid.NewGuid(), 1, 1, futureRequired, futureRequired)));

        await CompleteHello(dispatcher);
        var repeated = Assert.IsType<BrokerErrorResponse>(await Dispatch(
            dispatcher,
            new BrokerHelloRequest(Guid.NewGuid(), 1, 1, BrokerProtocolFeature.PreparedPlanDigest, 0)));

        Assert.Equal(BrokerErrorCode.HelloRejected, rejected.Code);
        Assert.Equal(BrokerErrorCode.ProtocolStateConflict, repeated.Code);
    }

    [Fact]
    public async Task PreparedPlanFlowsThroughStartStatusLeaseAndStopForAuthenticatedOwner()
    {
        var source = new FakePlanSource();
        var registry = new PreparedPlanRegistry();
        var runtime = new BrokerFakeRuntime();
        using var preparation = new BrokerPreparationCoordinator(source, registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), runtime);
        var counters = new BrokerCaptureHealth(12, 15, 1, 2, 0, 3, 64);
        var preview = new BrokerCapturePreview(1_000_000, 2, 1, 12, 0,
            [new(2, 40, Mechanism.Tcp, 9), new(1, 38, Mechanism.Tcp, 3)]);
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle, _ => counters, _ => preview);
        await CompleteHello(dispatcher);

        var prepareRequest = new BrokerPrepareCaptureRequest(
            "focused-transport",
            Mechanism.Tcp,
            [],
            false,
            false,
            Quota,
            BrokerRetentionPolicy.StopAtLimit,
            null);
        var prepared = Assert.IsType<BrokerPrepareCaptureResponse>(await Dispatch(
            dispatcher,
            prepareRequest));
        Assert.True(prepared.Prepared);
        Assert.Equal(Quota, prepared.Summary.Quota);
        Assert.Equal(prepared.Grant!.PlanDigest, source.LastPreparedDigest);

        var started = Assert.IsType<BrokerStartCaptureResponse>(await Dispatch(
            dispatcher,
            new BrokerStartCaptureRequest(prepared.Grant.Token, Guid.NewGuid())));
        Assert.Equal(BrokerOperationCode.Started, started.Code);
        CaptureId captureId = Assert.IsType<CaptureId>(started.CaptureId);

        var status = Assert.IsType<BrokerCaptureStatusResponse>(await Dispatch(
            dispatcher,
            new BrokerGetStatusRequest(captureId)));
        Assert.Equal(CaptureLifecycle.Recording, status.State);
        Assert.Equal(prepared.Grant.PlanDigest, status.PlanDigest);
        Assert.Equal(counters, status.Health);
        BrokerCapturePreview carried = Assert.IsType<BrokerCapturePreview>(status.Preview);
        Assert.Equal(preview.Counts, carried.Counts);
        Assert.Equal(preview with { Counts = carried.Counts }, carried);

        var renewed = Assert.IsType<BrokerRenewOwnerLeaseResponse>(await Dispatch(
            dispatcher,
            new BrokerRenewOwnerLeaseRequest(captureId)));
        Assert.Equal(BrokerOperationCode.LeaseRenewed, renewed.Code);

        var stopped = Assert.IsType<BrokerStopCaptureResponse>(await Dispatch(
            dispatcher,
            new BrokerStopCaptureRequest(captureId, Guid.NewGuid())));
        Assert.Equal(BrokerOperationCode.Stopped, stopped.Code);
        Assert.True(stopped.Milestones.FullyFinalized);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.StopCount);

        // A closed capture's loss is its ledger's and its records are published; neither live read is offered for it.
        var closed = Assert.IsType<BrokerCaptureStatusResponse>(await Dispatch(
            dispatcher,
            new BrokerGetStatusRequest(captureId)));
        Assert.Equal(CaptureLifecycle.Closed, closed.State);
        Assert.Null(closed.Health);
        Assert.Null(closed.Preview);
    }

    [Fact]
    public async Task LivePublicationIsStatedInTheEffectiveSummary()
    {
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(new FakePlanSource(), registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(
            registry, new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var owner = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(owner);

        var prepared = Assert.IsType<BrokerPrepareCaptureResponse>(await Dispatch(
            owner, ValidPrepare() with { Publication = BrokerJournalPublication.Live }));

        Assert.True(prepared.Prepared);
        Assert.Equal(BrokerJournalPublication.Live, prepared.Summary.Publication);
        Assert.Equal(
            BrokerJournalPublicationPolicy.IntervalMilliseconds(BrokerJournalPublication.Live, Quota.MaximumDurationSeconds),
            prepared.Summary.PublicationIntervalMilliseconds);
    }

    [Fact]
    public async Task PreparedTokenCannotCrossAuthenticatedOwners()
    {
        var source = new FakePlanSource();
        var registry = new PreparedPlanRegistry();
        var runtime = new BrokerFakeRuntime();
        using var preparation = new BrokerPreparationCoordinator(source, registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), runtime);
        var ownerA = CreateDispatcher(OwnerA, preparation, lifecycle);
        var ownerB = CreateDispatcher(OwnerB, preparation, lifecycle);
        await CompleteHello(ownerA);
        await CompleteHello(ownerB);

        var prepared = Assert.IsType<BrokerPrepareCaptureResponse>(await Dispatch(ownerA, ValidPrepare()));
        var rejected = Assert.IsType<BrokerStartCaptureResponse>(await Dispatch(
            ownerB,
            new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid())));

        Assert.Equal(BrokerOperationCode.PreparedTokenRejected, rejected.Code);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task ForeignStatusReturnsOneGenericUnavailableError()
    {
        var source = new FakePlanSource();
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(source, registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var ownerA = CreateDispatcher(OwnerA, preparation, lifecycle);
        var ownerB = CreateDispatcher(OwnerB, preparation, lifecycle);
        await CompleteHello(ownerA);
        await CompleteHello(ownerB);
        var prepared = Assert.IsType<BrokerPrepareCaptureResponse>(await Dispatch(ownerA, ValidPrepare()));
        var started = Assert.IsType<BrokerStartCaptureResponse>(await Dispatch(
            ownerA,
            new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid())));

        var foreign = Assert.IsType<BrokerErrorResponse>(await Dispatch(
            ownerB,
            new BrokerGetStatusRequest(started.CaptureId!.Value)));

        Assert.Equal(BrokerErrorCode.CaptureUnavailable, foreign.Code);
        Assert.DoesNotContain(started.CaptureId.Value.Value.ToString(), foreign.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonStartableLocalCompilationReturnsReviewableRefusalWithoutToken()
    {
        var source = new FakePlanSource();
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(source, registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(dispatcher);
        BrokerPrepareCaptureRequest request = ValidPrepare() with
        {
            FocusedProcessIds = [84],
            AllowBroaderCapture = false,
        };

        var refused = Assert.IsType<BrokerPrepareCaptureResponse>(await Dispatch(dispatcher, request));

        Assert.False(refused.Prepared);
        Assert.Null(refused.Grant);
        Assert.Equal(BrokerPrepareRefusalCode.PlanNotStartable, refused.RefusalCode);
        Assert.Null(refused.Summary.EffectiveProfileId);
        Assert.True(refused.Summary.BroaderCaptureNeedsConsent);
        Assert.False(refused.Summary.BroaderCaptureAccepted);
        Assert.Contains("consent", refused.Summary.Disclosure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateInFlightCorrelationIsRejectedWhileOriginalCompletesNormally()
    {
        var source = new FakePlanSource { HoldProbe = true };
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(dispatcher);
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame request = BrokerWireRequestCodec.Encode(new BrokerGetCapabilitiesRequest(), correlationId);

        Task<BrokerWireFrame> first = dispatcher.DispatchAsync(request).AsTask();
        await source.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        BrokerWireFrame duplicateFrame = await dispatcher.DispatchAsync(request);
        var duplicate = Assert.IsType<BrokerErrorResponse>(BrokerWireResponseCodec.Decode(duplicateFrame));
        source.ReleaseProbe.TrySetResult(true);
        BrokerWireFrame firstFrame = await first;

        Assert.Equal(BrokerErrorCode.DuplicateCorrelationId, duplicate.Code);
        Assert.IsType<BrokerCapabilitiesResponse>(BrokerWireResponseCodec.Decode(firstFrame));
    }

    [Fact]
    public async Task MalformedCommandBecomesBoundedTypedErrorWithSameCorrelation()
    {
        var source = new FakePlanSource();
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame malformed = BrokerWireFrame.CreateRequest(
            BrokerMessageType.GetStatus,
            correlationId,
            []);

        BrokerWireFrame response = await dispatcher.DispatchAsync(malformed);
        var error = Assert.IsType<BrokerErrorResponse>(BrokerWireResponseCodec.Decode(response));

        Assert.Equal(BrokerErrorCode.InvalidRequest, error.Code);
        Assert.Equal(correlationId, response.CorrelationId);
        Assert.True(error.UserMessage.Length <= 512);
    }

    [Fact]
    public async Task APrepareOutsideTheInstalledCatalogIsRefusedBeforeCompiling()
    {
        var source = new FakePlanSource();
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(dispatcher);

        var unknown = Assert.IsType<BrokerErrorResponse>(await Dispatch(dispatcher, new BrokerPrepareCaptureRequest(
            "not-installed", null, [], false, false, Quota, BrokerRetentionPolicy.StopAtLimit, null)));
        var misfocused = Assert.IsType<BrokerErrorResponse>(await Dispatch(dispatcher, new BrokerPrepareCaptureRequest(
            "explore", Mechanism.Tcp, [84], true, false, Quota, BrokerRetentionPolicy.StopAtLimit, null)));

        Assert.Equal(BrokerErrorCode.InvalidRequest, unknown.Code);
        Assert.Equal(BrokerErrorCode.InvalidRequest, misfocused.Code);
        Assert.Null(source.LastPreparedDigest);
    }

    [Fact]
    public async Task UnexpectedServiceFailureDoesNotLeakExceptionText()
    {
        var source = new FakePlanSource { ThrowOnProbe = true };
        using var preparation = new BrokerPreparationCoordinator(source, new(), Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(new(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = CreateDispatcher(OwnerA, preparation, lifecycle);
        await CompleteHello(dispatcher);

        var error = Assert.IsType<BrokerErrorResponse>(await Dispatch(
            dispatcher,
            new BrokerGetCapabilitiesRequest()));

        Assert.Equal(BrokerErrorCode.InternalFailure, error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("secret-fixture-path", error.UserMessage, StringComparison.Ordinal);
    }

    private static BrokerPrepareCaptureRequest ValidPrepare() => new(
        "focused-transport",
        Mechanism.Tcp,
        [],
        false,
        false,
        Quota,
        BrokerRetentionPolicy.StopAtLimit,
        null);

    private static BrokerConnectionDispatcher CreateDispatcher(
        BrokerClientIdentity owner,
        BrokerPreparationCoordinator preparation,
        BrokerLifecycleCoordinator lifecycle,
        Func<CaptureId, BrokerCaptureHealth?>? liveHealth = null,
        Func<CaptureId, BrokerCapturePreview?>? livePreview = null) =>
        new(
            owner,
            preparation,
            lifecycle,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "fixture-1",
            liveHealth: liveHealth,
            livePreview: livePreview);

    private static async Task CompleteHello(BrokerConnectionDispatcher dispatcher)
    {
        BrokerWireResponse response = await Dispatch(
            dispatcher,
            new BrokerHelloRequest(
                Guid.NewGuid(),
                1,
                1,
                BrokerHelloNegotiator.SupportedFeatures,
                BrokerProtocolFeature.PreparedPlanDigest));
        Assert.IsType<BrokerHelloResponse>(response);
    }

    private static async Task<BrokerWireResponse> Dispatch(
        BrokerConnectionDispatcher dispatcher,
        BrokerWireRequest request)
    {
        BrokerWireFrame frame = await dispatcher.DispatchAsync(
            BrokerWireRequestCodec.Encode(request, Guid.NewGuid()));
        return BrokerWireResponseCodec.Decode(frame);
    }

    private sealed class FakePlanSource : IBrokerCapturePlanSource
    {
        public bool HoldProbe { get; init; }
        public bool ThrowOnProbe { get; init; }
        public int ProbeCount { get; private set; }
        public string? LastPreparedDigest { get; private set; }
        public TaskCompletionSource<bool> ProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken)
        {
            ProbeCount++;
            ProbeEntered.TrySetResult(true);
            if (ThrowOnProbe)
            {
                throw new InvalidOperationException("secret-fixture-path");
            }

            if (HoldProbe)
            {
                await ReleaseProbe.Task.WaitAsync(cancellationToken);
            }

            return new()
            {
                ReportVersion = CapabilityInventoryProbe.ReportVersion,
                ProbedAtUtc = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero),
                Environment = new(
                    "Windows fixture",
                    Runtime.BuildId,
                    Runtime.Architecture,
                    true,
                    true,
                    "local machine")
                {
                    Support = new(BuildSupportTier.Primary, "Windows fixture", false),
                },
                AdapterVersion = Runtime.AdapterVersion,
                ProbeOnly = true,
                Sources = [],
                Mechanisms =
                [
                    new()
                    {
                        Mechanism = Mechanism.Tcp,
                        State = CapabilityState.Available,
                        Tier = CapabilityTier.TrafficVisualization,
                        Coverage = CoverageState.Covered,
                        Summary = "Measured TCP metadata.",
                        SourceIds = [WindowsSourceCatalog.KernelNetworkSourceId],
                    },
                ],
            };
        }

        public ValueTask<EffectiveCapturePlan> CompileAsync(
            CaptureProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EffectiveCapturePlan plan = CompileFocused(request.FocusedProcessIds, request.AllowBroaderCapture);
            LastPreparedDigest = BrokerPrepareCompiler.Prepare(
                plan,
                Quota,
                BrokerRetentionPolicy.StopAtLimit,
                Runtime).PreparedPlan?.Digest;
            return ValueTask.FromResult(plan);
        }
    }
}
