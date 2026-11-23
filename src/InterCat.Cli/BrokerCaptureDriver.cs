using System.Runtime.Versioning;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>A conversation with a capture broker: the launched broker's pipe in InterCat, a script in tests.</summary>
internal interface IBrokerExchange : IAsyncDisposable
{
    Task<BrokerWireResponse> SendAsync(BrokerWireRequest request, CancellationToken cancellationToken);
}

/// <summary>A broker launched for the agent, which an elevated agent starts without a prompt.</summary>
[SupportedOSPlatform("windows")]
internal sealed class LaunchedBroker(BrokerConnection connection) : IBrokerExchange
{
    public Task<BrokerWireResponse> SendAsync(BrokerWireRequest request, CancellationToken cancellationToken) =>
        connection.Client.SendAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>
/// The remote agent's capture on its host, through the host's capture broker as icat capture uses it (`broker-v1`): a
/// metadata profile, its duration and journal size, recorded live, the broker's evidence kept in its root for the
/// controller to fetch, and the broker's owner lease renewed by the agent while the controller's holds (ADR-117). The
/// broker is launched for the first capture and kept until the agent stops. A content capture is not served remotely.
/// </summary>
internal sealed class BrokerCaptureDriver(Func<CancellationToken, Task<IBrokerExchange>> connect) : IRemoteCaptureDriver
{
    /// <summary>The profiles an agent records for a controller: those that take no focus and keep no content.</summary>
    public static IReadOnlyList<string> Profiles { get; } =
        ["explore", "rpc-peers", "unix-sockets", "com-calls", "sync-objects", "window-messages", "sections", "sdk"];

    private const long FreeDiskBytes = 1_024L * 1_024 * 1_024;
    private readonly Dictionary<Guid, string> grants = [];
    private IBrokerExchange? broker;

    public async Task<DriverPreparation> PrepareAsync(RemoteCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Profiles.Contains(request.Profile, StringComparer.Ordinal))
        {
            throw new RemoteCaptureRefusedException(
                $"An agent records {string.Join(", ", Profiles)} for a controller, not '{request.Profile}': a focused or content "
                + "capture is not served remotely.");
        }

        IBrokerExchange exchange = await BrokerAsync(cancellationToken).ConfigureAwait(false);
        var prepare = new BrokerPrepareCaptureRequest(request.Profile, null, [], false, false,
            new BrokerCaptureQuota(checked((int)request.Seconds), request.JournalMebibytes * 1_024L * 1_024, FreeDiskBytes),
            BrokerRetentionPolicy.StopAtLimit, null, BrokerJournalPublication.Live);
        BrokerWireResponse answer = await exchange.SendAsync(prepare, cancellationToken).ConfigureAwait(false);
        if (answer is not BrokerPrepareCaptureResponse prepared)
        {
            throw new RemoteCaptureRefusedException("The host's capture broker did not prepare the capture: " + Describe(answer));
        }

        if (!prepared.Prepared || prepared.Grant is not { } grant)
        {
            throw new RemoteCaptureRefusedException("The host's capture broker cannot record it: " + (prepared.RefusalReason ?? "it gave no reason") + ".");
        }

        var id = Guid.NewGuid();
        grants[id] = grant.Token;
        BrokerEffectiveCaptureSummary summary = prepared.Summary;
        return new DriverPreparation(id, summary.EffectiveProfileId ?? summary.RequestedProfileId,
            checked((uint)summary.Quota.MaximumDurationSeconds), checked((uint)(summary.Quota.MaximumJournalBytes / (1_024 * 1_024))),
            summary.CollectionStatement);
    }

    public async Task<Guid> StartAsync(Guid prepared, CancellationToken cancellationToken)
    {
        if (!grants.Remove(prepared, out string? token) || broker is null)
        {
            throw new RemoteCaptureRefusedException("The host holds no such prepared capture.");
        }

        BrokerWireResponse answer = await broker.SendAsync(new BrokerStartCaptureRequest(token, Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        return answer is BrokerStartCaptureResponse { Code: BrokerOperationCode.Started, CaptureId: { } captureId }
            ? captureId.Value
            : throw new RemoteCaptureRefusedException("The host's capture did not start: " + (answer is BrokerStartCaptureResponse failed
                ? failed.FailureReason ?? failed.Code.ToString()
                : Describe(answer)));
    }

    public Task ReleaseAsync(Guid prepared, CancellationToken cancellationToken)
    {
        // A grant not used lapses in the broker on its own (broker-v1 §2); the agent forgets it.
        _ = grants.Remove(prepared);
        return Task.CompletedTask;
    }

    public async Task<DriverCaptureState> StatusAsync(Guid capture, CancellationToken cancellationToken)
    {
        BrokerWireResponse answer = await Broker().SendAsync(new BrokerGetStatusRequest(new CaptureId(capture)), cancellationToken)
            .ConfigureAwait(false);
        return answer is BrokerCaptureStatusResponse status
            ? new DriverCaptureState(Lifecycle(status.State), status.Health?.AdmittedRecords ?? 0, Lost(status.Health),
                status.StopMilestones.FullyFinalized, status.FailureReason)
            : throw new RemoteCaptureRefusedException("The host's capture broker did not say where the capture stands: " + Describe(answer));
    }

    public async Task RenewAsync(Guid capture, CancellationToken cancellationToken)
    {
        BrokerWireResponse answer = await Broker().SendAsync(new BrokerRenewOwnerLeaseRequest(new CaptureId(capture)), cancellationToken)
            .ConfigureAwait(false);
        if (answer is not BrokerRenewOwnerLeaseResponse { Code: BrokerOperationCode.LeaseRenewed })
        {
            throw new RemoteCaptureRefusedException("The host's capture broker did not renew the capture: " + (answer is BrokerRenewOwnerLeaseResponse renewal
                ? renewal.FailureReason ?? renewal.Code.ToString()
                : Describe(answer)));
        }
    }

    public async Task<DriverCaptureState> StopAsync(Guid capture, CancellationToken cancellationToken)
    {
        BrokerWireResponse answer = await Broker().SendAsync(new BrokerStopCaptureRequest(new CaptureId(capture), Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        if (answer is not BrokerStopCaptureResponse { Code: BrokerOperationCode.Stopped or BrokerOperationCode.AlreadyStopped })
        {
            throw new RemoteCaptureRefusedException("The host's capture did not stop: " + (answer is BrokerStopCaptureResponse failed
                ? failed.FailureReason ?? failed.Code.ToString()
                : Describe(answer)));
        }

        return await StatusAsync(capture, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (broker is not null)
        {
            await broker.DisposeAsync().ConfigureAwait(false);
            broker = null;
        }
    }

    private async Task<IBrokerExchange> BrokerAsync(CancellationToken cancellationToken)
    {
        if (broker is not null)
        {
            return broker;
        }

        IBrokerExchange launched = await connect(cancellationToken).ConfigureAwait(false);
        BrokerWireResponse hello = await launched.SendAsync(
                new BrokerHelloRequest(Guid.NewGuid(), 1, 1, BrokerHelloNegotiator.SupportedFeatures, BrokerProtocolFeature.PreparedPlanDigest),
                cancellationToken).ConfigureAwait(false);
        if (hello is not BrokerHelloResponse)
        {
            await launched.DisposeAsync().ConfigureAwait(false);
            throw new RemoteCaptureRefusedException("The host's capture broker does not speak a compatible protocol version.");
        }

        broker = launched;
        return launched;
    }

    private IBrokerExchange Broker() =>
        broker ?? throw new RemoteCaptureRefusedException("The host's capture broker is not running for this agent.");

    private static RemoteCaptureLifecycle Lifecycle(CaptureLifecycle state) => state switch
    {
        CaptureLifecycle.Recording => RemoteCaptureLifecycle.Recording,
        CaptureLifecycle.Stopping or CaptureLifecycle.Finalizing => RemoteCaptureLifecycle.Stopping,
        CaptureLifecycle.Closed => RemoteCaptureLifecycle.Closed,
        _ => RemoteCaptureLifecycle.Starting,
    };

    private static long Lost(BrokerCaptureHealth? health) =>
        health is null ? 0 : health.ApplicationDrops + (health.ProviderReportedLoss ?? 0) + (health.ConsumerBufferLoss ?? 0);

    private static string Describe(BrokerWireResponse answer) =>
        answer is BrokerErrorResponse error ? error.UserMessage : $"it answered {answer.MessageType}";
}
