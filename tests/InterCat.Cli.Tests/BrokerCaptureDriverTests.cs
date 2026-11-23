using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Remote;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// The remote agent's capture on its host goes through the host's capture broker as icat capture does (ADR-117,
/// `broker-v1`): asked here of a broker that answers from a script, so no test launches a broker or records.
/// </summary>
public sealed class BrokerCaptureDriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§15: an agent asks its host's broker as icat capture does - a metadata profile recorded live within its duration and journal, the grant it prepared started, its owner lease renewed and its stop asked - and says what the broker counted")]
    public async Task AnAgentDrivesItsHostsBroker()
    {
        var capture = new CaptureId(Guid.NewGuid());
        var broker = new ScriptedBroker(request => request switch
        {
            BrokerHelloRequest => new BrokerHelloResponse(Guid.NewGuid(), 1, 1, BrokerProtocolFeature.PreparedPlanDigest, "test"),
            BrokerPrepareCaptureRequest => new BrokerPrepareCaptureResponse(true, null, null,
                new PreparedPlanGrant("grant-1", "sha256:plan", Now, Now.AddSeconds(30)), Summary(60, 1_024)),
            BrokerStartCaptureRequest => new BrokerStartCaptureResponse(BrokerOperationCode.Started, capture, CaptureLifecycle.Recording, Now.AddSeconds(30), null),
            BrokerRenewOwnerLeaseRequest => new BrokerRenewOwnerLeaseResponse(BrokerOperationCode.LeaseRenewed, capture, CaptureLifecycle.Recording, Now.AddSeconds(60), null),
            BrokerStopCaptureRequest => new BrokerStopCaptureResponse(BrokerOperationCode.Stopped, capture, CaptureLifecycle.Closed, Finished, null),
            BrokerGetStatusRequest => new BrokerCaptureStatusResponse(capture, StoppedYet ? CaptureLifecycle.Closed : CaptureLifecycle.Recording, "sha256:plan",
                Now, Now, Now.AddSeconds(30), StoppedYet ? Finished : BrokerStopMilestones.None, null, null,
                new BrokerCaptureHealth(500, 510, 2, 3, null, 0, 1_024)),
            _ => throw new InvalidOperationException("unexpected " + request.GetType().Name),
        });
        await using var driver = new BrokerCaptureDriver(_ => Task.FromResult<IBrokerExchange>(broker));

        DriverPreparation prepared = await driver.PrepareAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None);
        BrokerPrepareCaptureRequest asked = Assert.Single(broker.Asked.OfType<BrokerPrepareCaptureRequest>());
        Assert.Equal(("explore", (Mechanism?)null, 0, false, (ContentCaptureRequest?)null), (asked.ProfileId, asked.FocusedMechanism, asked.FocusedProcessIds.Count, asked.AllowBroaderCapture, asked.Content));
        Assert.Equal((new BrokerCaptureQuota(60, 1_024L << 20, 1_024L << 20), BrokerRetentionPolicy.StopAtLimit, BrokerJournalPublication.Live),
            (asked.Quota, asked.Retention, asked.Publication));
        Assert.Equal(("explore", 60u, 1_024u, "Process lifecycle and TCP transport events."),
            (prepared.Profile, prepared.Seconds, prepared.JournalMebibytes, prepared.Collects));
        Assert.IsType<BrokerHelloRequest>(broker.Asked[0]);

        Assert.Equal(capture.Value, await driver.StartAsync(prepared.Prepared, CancellationToken.None));
        Assert.Equal("grant-1", Assert.Single(broker.Asked.OfType<BrokerStartCaptureRequest>()).PreparedToken);

        DriverCaptureState recording = await driver.StatusAsync(capture.Value, CancellationToken.None);
        Assert.Equal((RemoteCaptureLifecycle.Recording, 500L, 5L, false), (recording.State, recording.Records, recording.Lost, recording.Finalized));

        await driver.RenewAsync(capture.Value, CancellationToken.None);
        Assert.Equal(capture, Assert.Single(broker.Asked.OfType<BrokerRenewOwnerLeaseRequest>()).CaptureId);

        StoppedYet = true;
        DriverCaptureState stopped = await driver.StopAsync(capture.Value, CancellationToken.None);
        Assert.Equal(capture, Assert.Single(broker.Asked.OfType<BrokerStopCaptureRequest>()).CaptureId);
        Assert.Equal((RemoteCaptureLifecycle.Closed, true), (stopped.State, stopped.Finalized));

        // One broker for the agent, launched once and let go when the agent stops.
        Assert.Single(broker.Asked.OfType<BrokerHelloRequest>());
        Assert.False(broker.Disposed);
        await driver.DisposeAsync();
        Assert.True(broker.Disposed);
    }

    [Fact(DisplayName = "§15: an agent records no focused or content capture for a controller, launching no broker for one, and says in the broker's own words why its host cannot record a profile")]
    public async Task AnAgentRefusesWhatItsHostWillNotRecord()
    {
        int launched = 0;
        var broker = new ScriptedBroker(request => request switch
        {
            BrokerHelloRequest => new BrokerHelloResponse(Guid.NewGuid(), 1, 1, BrokerProtocolFeature.PreparedPlanDigest, "test"),
            BrokerPrepareCaptureRequest => new BrokerPrepareCaptureResponse(false, BrokerPrepareRefusalCode.PlanNotStartable,
                "this build declares no provider for it", null, Summary(60, 1_024)),
            _ => throw new InvalidOperationException("unexpected " + request.GetType().Name),
        });
        await using var driver = new BrokerCaptureDriver(_ =>
        {
            launched++;
            return Task.FromResult<IBrokerExchange>(broker);
        });

        foreach (string profile in new[] { "content", "focused-transport" })
        {
            RemoteCaptureRefusedException notServed = await Assert.ThrowsAsync<RemoteCaptureRefusedException>(() =>
                driver.PrepareAsync(new RemoteCaptureRequest(profile, 60, 1_024), CancellationToken.None));
            Assert.Contains("not served remotely", notServed.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, launched);
        RemoteCaptureRefusedException refused = await Assert.ThrowsAsync<RemoteCaptureRefusedException>(() =>
            driver.PrepareAsync(new RemoteCaptureRequest("sections", 60, 1_024), CancellationToken.None));
        Assert.Contains("this build declares no provider for it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, launched);
    }

    private bool StoppedYet { get; set; }

    private static BrokerStopMilestones Finished => new(true, true, true, true, true);

    private static BrokerEffectiveCaptureSummary Summary(int seconds, long mebibytes) => new(
        "explore", "explore", AdmissionMode.MetadataOnly, AdmissionMode.MetadataOnly,
        null, null, [], [], false, false, false,
        [new BrokerEffectiveSourceSummary("TCP", ProviderProcessScope.WholeMachineFilterUnavailable, [], false, "System-wide")],
        "No payload contents.", "Process lifecycle and TCP transport events.",
        new BrokerCaptureQuota(seconds, mebibytes << 20, 1_024L << 20), BrokerRetentionPolicy.StopAtLimit, [],
        BrokerJournalPublication.Live, 2_000);

    /// <summary>A broker answering from a script, keeping what it was asked.</summary>
    private sealed class ScriptedBroker(Func<BrokerWireRequest, BrokerWireResponse> answer) : IBrokerExchange
    {
        public List<BrokerWireRequest> Asked { get; } = [];

        public bool Disposed { get; private set; }

        public Task<BrokerWireResponse> SendAsync(BrokerWireRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(answer(request));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
