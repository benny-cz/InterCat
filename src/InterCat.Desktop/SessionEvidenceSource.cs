using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// Where the viewer reads evidence rows and original records for the session it shows. Every read acquires its own
/// lease off the UI thread; pages continue by row, so a live publication between two pages does not restart them.
/// </summary>
public sealed class SessionEvidenceSource(string sessionPath, Guid sessionId, long generation)
{
    private readonly Lock storeGate = new();
    private SessionStore? store;

    public string SessionPath { get; } = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));

    public Guid SessionId { get; } = sessionId;

    /// <summary>The generation the workspace was projected from; a page may continue in a newer one.</summary>
    public long Generation { get; } = generation;

    /// <summary>Counts each edge's and channel's records inside an analysis interval, for a brushed ranking.</summary>
    public Task<SessionIntervalCounts> CountAsync(TimeRange interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalQuery.Count(
            Store(),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's transport bytes over the whole session, or over <paramref name="interval"/>, for the ranked table's
    /// byte ranking (<see cref="SessionByteRanking"/>).
    /// </summary>
    public Task<SessionByteMeasures> ByteMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionByteRanking.Measure(
            Store(),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's RPC calls made and served over the whole session, or over <paramref name="interval"/>, for the
    /// ranked table's call ranking (<see cref="SessionCallRanking"/>).
    /// </summary>
    public Task<SessionCallMeasures> CallMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionCallRanking.Measure(
            Store(),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's and group's distinct peers over the whole session, or over <paramref name="interval"/>, for the
    /// ranked table's peer ranking (<see cref="SessionPeerRanking"/>).
    /// </summary>
    public Task<SessionPeerMeasures> PeerMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionPeerRanking.Measure(
            Store(),
            interval,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// What each column of an interval's records sent and received, over the records one listing of the interval table
    /// counts (<see cref="SessionIntervalByteQuery"/>).
    /// </summary>
    public Task<SessionIntervalByteMeasures> IntervalBytesAsync(
        TimeRange interval, int columns, IntervalByteScope scope, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalByteQuery.Measure(
            Store(),
            interval,
            columns,
            scope,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>The timeline over a viewport at the resolution it is drawn at, for zoomed detail.</summary>
    public Task<SessionTimelineDetail> TimelineAsync(TimeRange interval, int columns, CancellationToken cancellationToken) =>
        Task.Run(() => SessionTimelineQuery.Detail(
            Store(),
            interval,
            columns,
            cancellationToken), cancellationToken);

    /// <summary>The timeline over a viewport together with the records a focused rung reads, counted in one pass.</summary>
    public Task<SessionFocusedTimeline> FocusedTimelineAsync(
        TimeRange interval, int columns, TimelineFocus focus, CancellationToken cancellationToken) =>
        Task.Run(() => SessionTimelineQuery.Focused(
            Store(),
            interval,
            columns,
            focus,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// One process instance's RPC channels in the current generation (`contracts/operations-v1.md` §5), counting the calls
    /// the whole session holds, or <paramref name="interval"/> does.
    /// </summary>
    public Task<RpcChannelList> RpcChannelsAsync(ProcessInstanceId instance, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.Channels(Store(), instance, interval, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>One RPC channel's calls in reading order, from <paramref name="offset"/>, one page, within <paramref name="interval"/> if given.</summary>
    public Task<RpcCallPage> RpcCallsAsync(string channelKey, int offset, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.Calls(Store(), channelKey, offset, interval: interval, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>
    /// A call's channel's calls in reading order from the first through the page that holds the call, within
    /// <paramref name="interval"/> if given; the first page alone when the call is not listed there or lies too far down.
    /// </summary>
    public Task<RpcCallPage> RpcCallsThroughAsync(string callKey, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.CallsThrough(Store(), callKey, interval: interval, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>
    /// One process instance's HTTP exchanges in the current generation, as one channel (ADR-037), counting the exchanges
    /// the whole session holds, or <paramref name="interval"/> does.
    /// </summary>
    public Task<HttpChannelList> HttpChannelsAsync(ProcessInstanceId instance, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionHttpExchanges.Channels(Store(), instance, interval, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>One process's HTTP exchanges in reading order, from <paramref name="offset"/>, one page, within <paramref name="interval"/> if given.</summary>
    public Task<HttpExchangePage> HttpExchangesAsync(string channelKey, int offset, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionHttpExchanges.Exchanges(Store(), channelKey, offset, interval: interval, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>One RPC channel's calls within an interval, for the timeline's call lane.</summary>
    public Task<RpcCallSpanPage> RpcSpansAsync(string channelKey, TimeRange interval, int columns, CancellationToken cancellationToken) =>
        Task.Run(
            () => SessionRpcCalls.Spans(Store(), channelKey, interval, columns: columns, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>
    /// The session's store, shared by every read and by every workspace of this session (<see cref="SharedSessionStores"/>).
    /// Opening verifies the pointer, the manifest and every file it names, which for a large session costs far more than a
    /// timeline or page read itself; each read still leases whichever generation is current. A failed open is not kept,
    /// so the next read tries again.
    /// </summary>
    private SessionStore Store()
    {
        lock (storeGate)
        {
            return store ??= SharedSessionStores.Open(SessionPath, SessionId);
        }
    }

    /// <summary>Every record of a scope up to a limit, in one pass under one lease, for an export of the whole scope.</summary>
    public Task<SessionEvidencePage> ReadScopeAsync(EvidenceScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.ReadScope(
            Store(),
            limit,
            scope.ChannelKey,
            scope.Interval,
            scope.OwnerProcesses.Count == 0 ? null : scope.OwnerProcesses,
            resolveOwners: true,
            cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<SessionEvidencePage> ReadAsync(EvidenceScope scope, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.Read(
            Store(),
            scope.ChannelKey,
            scope.Interval,
            pageSize: SessionEvidenceQuery.DefaultPageSize,
            cursor: cursor,
            ownerProcesses: scope.OwnerProcesses.Count == 0 ? null : scope.OwnerProcesses,
            resolveOwners: true,
            operationKey: scope.OperationKey,
            cancellationToken: cancellationToken), cancellationToken);
    }
}
