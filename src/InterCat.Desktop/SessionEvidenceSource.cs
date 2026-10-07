using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// Where the viewer reads evidence rows and original records for the session it shows. Every read acquires its own
/// lease off the UI thread; pages continue by row, so a live publication between two pages does not restart them. Every
/// read that binds a record to a process does so under one evidence policy, the one the shown overview was projected
/// under, so a count, a ranking and the records E lists never disagree about whose a record is.
/// </summary>
public sealed class SessionEvidenceSource(
    string sessionPath, Guid sessionId, long generation, EvidencePolicy policy = EvidencePolicy.IncludeCorrelated)
{
    private readonly Lock storeGate = new();
    private SessionStore? store;

    public string SessionPath { get; } = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));

    public Guid SessionId { get; } = sessionId;

    /// <summary>The generation the workspace was projected from; a page may continue in a newer one.</summary>
    public long Generation { get; } = generation;

    /// <summary>
    /// How strongly a record must bind to a process for a read to count it as that process's: correlated by default, or
    /// candidates too, when a person chose to count a reused PID's later holders' records.
    /// </summary>
    public EvidencePolicy Policy { get; } = Enum.IsDefined(policy) ? policy : throw new ArgumentOutOfRangeException(nameof(policy));

    /// <summary>
    /// The instances the shown view sets aside as InterCat's own (§19.5's view filter), which a ranking's measures leave out
    /// of every total over the rows shown while each keeps its own; none unless a person set them aside.
    /// </summary>
    public IReadOnlySet<ProcessInstanceId> SetAside { get; init; } = new HashSet<ProcessInstanceId>();

    /// <summary>Counts each edge's and channel's records inside an analysis interval, for a brushed ranking.</summary>
    public Task<SessionIntervalCounts> CountAsync(TimeRange interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalQuery.Count(
            Store(),
            interval,
            Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's transport bytes over the whole session, or over <paramref name="interval"/>, for the ranked table's
    /// byte ranking (<see cref="SessionByteRanking"/>).
    /// </summary>
    public Task<SessionByteMeasures> ByteMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionByteRanking.Measure(
            Store(),
            interval,
            policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's RPC calls made and served over the whole session, or over <paramref name="interval"/>, for the
    /// ranked table's call ranking (<see cref="SessionCallRanking"/>).
    /// </summary>
    public Task<SessionCallMeasures> CallMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionCallRanking.Measure(
            Store(),
            interval,
            policy: Policy,
            setAside: SetAside,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// Each process's and group's distinct peers over the whole session, or over <paramref name="interval"/>, for the
    /// ranked table's peer ranking (<see cref="SessionPeerRanking"/>).
    /// </summary>
    public Task<SessionPeerMeasures> PeerMeasuresAsync(TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionPeerRanking.Measure(
            Store(),
            interval,
            policy: Policy,
            setAside: SetAside,
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
            policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// What each mechanism lane's records sent and received in each column of an interval, for the timeline to plot under a
    /// byte ranking (<see cref="SessionIntervalByteQuery.MeasureByMechanism"/>).
    /// </summary>
    public Task<SessionMechanismByteMeasures> LaneBytesAsync(
        TimeRange interval, int columns, IReadOnlyList<Mechanism> mechanisms, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalByteQuery.MeasureByMechanism(
            Store(),
            interval,
            columns,
            mechanisms,
            cancellationToken), cancellationToken);

    /// <summary>
    /// What a group's lanes' records sent and received in each column, and every record in the machine row's columns, for
    /// the process lanes to plot under a byte ranking (<see cref="SessionIntervalByteQuery.MeasureByOwner"/>).
    /// </summary>
    public Task<SessionOwnerByteMeasures> OwnerBytesAsync(
        TimeRange interval, int columns, int laneColumns, IReadOnlyList<ProcessInstanceId> owners,
        CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalByteQuery.MeasureByOwner(
            Store(),
            interval,
            columns,
            laneColumns,
            owners,
            policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// What a process's records sent and received in each column by source direction, and every record in the machine
    /// row, for its direction rows to plot under a byte ranking (<see cref="SessionIntervalByteQuery.MeasureByDirection"/>).
    /// </summary>
    public Task<SessionDirectionByteMeasures> DirectionBytesAsync(
        TimeRange interval, int columns, ProcessInstanceId owner, CancellationToken cancellationToken) =>
        Task.Run(() => SessionIntervalByteQuery.MeasureByDirection(
            Store(),
            interval,
            columns,
            owner,
            policy: Policy,
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
            policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// One process instance's RPC channels in the current generation (`contracts/operations-v1.md` §5), counting the calls
    /// the whole session holds, or <paramref name="interval"/> does.
    /// </summary>
    public Task<RpcChannelList> RpcChannelsAsync(ProcessInstanceId instance, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.Channels(Store(), instance, interval, policy: Policy, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>One RPC channel's calls in reading order, from <paramref name="offset"/>, one page, within <paramref name="interval"/> if given.</summary>
    public Task<RpcCallPage> RpcCallsAsync(string channelKey, int offset, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.Calls(Store(), channelKey, offset, interval: interval, policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// A call's channel's calls in reading order from the first through the page that holds the call, within
    /// <paramref name="interval"/> if given; the first page alone when the call is not listed there or lies too far down.
    /// </summary>
    public Task<RpcCallPage> RpcCallsThroughAsync(string callKey, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionRpcCalls.CallsThrough(Store(), callKey, interval: interval, policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// One process instance's HTTP exchanges in the current generation, as one channel (ADR-037), counting the exchanges
    /// the whole session holds, or <paramref name="interval"/> does.
    /// </summary>
    public Task<HttpChannelList> HttpChannelsAsync(ProcessInstanceId instance, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionHttpExchanges.Channels(Store(), instance, interval, policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>One process's HTTP exchanges in reading order, from <paramref name="offset"/>, one page, within <paramref name="interval"/> if given.</summary>
    public Task<HttpExchangePage> HttpExchangesAsync(string channelKey, int offset, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionHttpExchanges.Exchanges(Store(), channelKey, offset, interval: interval, policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>
    /// One process instance's one-sided connections in the current generation - the TCP connections and UDP flows whose
    /// other end no record holds - with their records and bytes, in the whole session or <paramref name="interval"/>.
    /// </summary>
    public Task<ConnectionList> ConnectionsAsync(ProcessInstanceId instance, TimeRange? interval, CancellationToken cancellationToken) =>
        Task.Run(() => SessionConnections.OneSided(Store(), instance, interval, policy: Policy, cancellationToken: cancellationToken),
            cancellationToken);

    /// <summary>
    /// A process's HTTP exchanges within a viewport for the timeline's exchange lane, one by one or as density
    /// (<see cref="SessionHttpExchanges.Spans"/>).
    /// </summary>
    public Task<HttpExchangeSpanPage> HttpSpansAsync(
        string channelKey, TimeRange viewport, int columns, CancellationToken cancellationToken) =>
        Task.Run(() => SessionHttpExchanges.Spans(Store(), channelKey, viewport, columns: columns, policy: Policy,
            cancellationToken: cancellationToken), cancellationToken);

    /// <summary>One RPC channel's calls within an interval, for the timeline's call lane.</summary>
    public Task<RpcCallSpanPage> RpcSpansAsync(string channelKey, TimeRange interval, int columns, CancellationToken cancellationToken) =>
        Task.Run(
            () => SessionRpcCalls.Spans(Store(), channelKey, interval, columns: columns, policy: Policy,
                cancellationToken: cancellationToken),
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
        return Task.Run(() => SessionEvidenceQuery.ReadScope(Store(), scope, limit, Policy, cancellationToken), cancellationToken);
    }

    public Task<SessionEvidencePage> ReadAsync(EvidenceScope scope, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.Run(() => SessionEvidenceQuery.Read(Store(), scope, cursor, Policy, cancellationToken), cancellationToken);
    }
}
