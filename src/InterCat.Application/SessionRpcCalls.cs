using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One RPC channel as the ladder shows it: one process instance's calls on one side to one interface
/// (`contracts/operations-v1.md` §5). It belongs to that one process, so its counts are its own and a total over a
/// process's channels adds up.
/// </summary>
public sealed record RpcChannelSummary(
    string Key,
    ProcessInstanceId Instance,
    RpcCallSide Side,
    Guid? Interface,
    RpcCallCounts Counts,
    RpcCallDurations? Durations,
    long Records)
{
    /// <summary>Who is at the other end of the channel's calls; null when the capture collected no ALPC.</summary>
    public RpcChannelPeers? Peers { get; init; }

    /// <summary>The channel in words: which side's calls, and to what.</summary>
    public string Name => (Side == RpcCallSide.Client ? "RPC calls to " : "RPC calls served on ") + RpcInterfaceNames.Describe(Interface);

    /// <summary>What its calls came to, in one line: completions, failures, open calls and the median duration.</summary>
    public string Outcome(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        var parts = new List<string>
        {
            string.Create(format, $"{Counts.Calls:N0} {(Counts.Calls == 1 ? "call" : "calls")}"),
        };
        if (Counts.Failed > 0) parts.Add(string.Create(format, $"{Counts.Failed:N0} failed"));
        if (Counts.OpenAtCaptureEnd > 0) parts.Add(string.Create(format, $"{Counts.OpenAtCaptureEnd:N0} open at capture end"));
        long unpaired = Counts.StartNotObserved + Counts.NoActivityId + Counts.Ambiguous;
        if (unpaired > 0) parts.Add(string.Create(format, $"{unpaired:N0} unpaired"));
        if (Durations is { } durations) parts.Add("median " + OperationText.Duration(durations.Median, format));
        return string.Join(" · ", parts);
    }
}

/// <summary>A process instance's RPC channels in one generation, the one with the most call records first.</summary>
public sealed record RpcChannelList(Guid SessionId, long Generation, IReadOnlyList<RpcChannelSummary> Channels);

/// <summary>One call of a channel, with the key that names it in every generation holding it.</summary>
public sealed record RpcCallRow(string Key, RpcCall Call)
{
    /// <summary>The call's other end, or why none is known; null when the capture collected no ALPC.</summary>
    public RpcCallPeerView? OtherEnd { get; init; }
}

/// <summary>A process at the other end of RPC calls, and how many of them reached it (`contracts/operations-v1.md` §5c).</summary>
public sealed record RpcChannelPeer(int ProcessId, ProcessInstanceId? Instance, string? Image, Guid? Interface, long Calls)
{
    /// <summary>The process as a row names it: its image and PID, or its PID alone where no executable was witnessed.</summary>
    public string Name => Image is { } image
        ? string.Create(CultureInfo.InvariantCulture, $"{image} · {ProcessId}")
        : string.Create(CultureInfo.InvariantCulture, $"PID {ProcessId}");
}

/// <summary>
/// Who is at the other end of an RPC channel's calls: for a client channel the processes that served them and why the
/// rest are unresolved, for a server channel the processes whose calls it served (`contracts/operations-v1.md` §5c).
/// </summary>
public sealed record RpcChannelPeers(
    long Linked,
    IReadOnlyDictionary<RpcPeerState, long> Unresolved,
    IReadOnlyList<RpcChannelPeer> Processes)
{
    /// <summary>
    /// The other end in one phrase - "served by services.exe · 1960 (1,499 of 1,500)" - or null when nothing is known of
    /// it, as for a server channel no linked call reached.
    /// </summary>
    public string? Describe(RpcCallSide side, long calls, IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        if (Processes.Count == 0)
        {
            return side == RpcCallSide.Client && Unresolved.Count > 0
                ? "other end unresolved: " + OperationText.PeerState(Unresolved.MaxBy(entry => entry.Value).Key)
                : null;
        }

        string others = Processes.Count > 1 ? string.Create(format, $" and {Processes.Count - 1:N0} more") : string.Empty;
        string verb = side == RpcCallSide.Client ? "served by" : "called by";
        return string.Create(format, $"{verb} {Processes[0].Name}{others} ({Linked:N0} of {calls:N0})");
    }
}

/// <summary>
/// One call's other end: the process of the call that served it, or of the client call it served, with that call's key;
/// or why none is known (`contracts/operations-v1.md` §5c).
/// </summary>
public sealed record RpcCallPeerView(RpcPeerState? Unresolved, RpcChannelPeer? Process, string? CallKey)
{
    /// <summary>When the call at the other end began - its start, or its stop without one - in session nanoseconds.</summary>
    public long? FirstNanoseconds { get; init; }

    /// <summary>The other end in one phrase, or null for a server call no client call is known to have made.</summary>
    public string? Describe(RpcCallSide side) => Process is { } peer
        ? (side == RpcCallSide.Client ? "served by " : "called by ") + peer.Name
        : Unresolved is { } state ? "other end unresolved: " + OperationText.PeerState(state) : null;
}

/// <summary>
/// A page of one channel's calls in reading order. A channel this generation no longer holds is a problem stated, not an
/// empty page.
/// </summary>
public sealed record RpcCallPage(
    Guid SessionId,
    long Generation,
    RpcChannelSummary? Channel,
    int Offset,
    IReadOnlyList<RpcCallRow> Calls,
    bool More,
    string? Problem);

/// <summary>
/// One call as the timeline draws it, in workspace ticks (100 ns, session-relative): a start or a stop may be unknown,
/// and a call open at capture end has no end.
/// </summary>
public sealed record RpcCallSpanView(
    string Key,
    long? StartTicks,
    long? EndTicks,
    RpcCallState State,
    long? Status,
    long? Procedure)
{
    /// <summary>Where the call begins on the axis: its start, or its stop when no start was seen.</summary>
    public long FirstTicks => StartTicks ?? EndTicks!.Value;

    /// <summary>Whether it completed with a status other than 0.</summary>
    public bool Failed => State == RpcCallState.Completed && Status is { } code && code != 0;
}

/// <summary>
/// A channel's calls within one interval, in reading order, when they are no more than a lane draws one by one; otherwise
/// none of them, and their <see cref="Density"/> instead (§6.2). <see cref="Total"/> counts every call the interval holds.
/// </summary>
public sealed record RpcCallSpanPage(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyList<RpcCallSpanView> Calls,
    long Total,
    string? Problem)
{
    /// <summary>The calls as density columns, when there were more of them than the budget; null otherwise.</summary>
    public RpcCallDensity? Density { get; init; }
}

/// <summary>
/// A channel's calls within one interval as §6.2's density regime draws them, when there are more than a lane draws one by
/// one: the interval cut into equal columns, and in each the calls running in it and how many of those failed. A call
/// runs in every column from its start's to its stop's; a call open at capture end runs to the interval's end, and a call
/// with one record runs in that record's column. It is the overlap count of §21.1, never a count of records.
/// </summary>
public sealed record RpcCallDensity(TimeRange Interval, IReadOnlyList<long> Running, IReadOnlyList<long> Failed)
{
    /// <summary>The most calls running in any one column: what the lane's intensity scale reaches.</summary>
    public long Maximum { get; } = Running.Count == 0 ? 0 : Running.Max();

    public int Columns => Running.Count;

    /// <summary>The interval one column covers: equal shares of <see cref="Interval"/>, the last one ending with it.</summary>
    public TimeRange ColumnInterval(int column) => ColumnIntervalOf(Interval, Columns, column);

    /// <summary>
    /// The interval column <paramref name="column"/> of <paramref name="columns"/> covers: equal shares of
    /// <paramref name="interval"/>, the last one ending with it - any lane's density columns, an RPC channel's or an HTTP
    /// channel's.
    /// </summary>
    public static TimeRange ColumnIntervalOf(TimeRange interval, int columns, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, columns);
        return new(Boundary(interval, columns, column), Boundary(interval, columns, column + 1));
    }

    /// <summary>The column a tick falls in, clamped to the interval's first and last.</summary>
    public static int ColumnOf(TimeRange interval, int columns, long tick) => tick <= interval.StartTicks
        ? 0
        : tick >= interval.EndTicks
            ? columns - 1
            : (int)((Int128)(tick - interval.StartTicks) * columns / interval.SpanTicks);

    /// <summary>Where a column begins: the first tick <see cref="ColumnOf"/> places in it, so the two never disagree.</summary>
    private static long Boundary(TimeRange interval, int columns, int column) =>
        interval.StartTicks + (long)((((Int128)interval.SpanTicks * column) + columns - 1) / columns);
}

/// <summary>
/// Reads a published session's RPC calls for the ladder: a process's channels, and a channel's calls a page at a time.
/// Each read leases the current generation, whose calls are paired once and shared by every read of it.
/// </summary>
public static class SessionRpcCalls
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 500;

    /// <summary>The most calls <see cref="CallsThrough"/> lists to reach the one it names; past them it reads the first page.</summary>
    public const int MaximumListedThrough = 5_000;

    /// <summary>The most calls one timeline read returns one by one; a denser interval is read as density columns.</summary>
    public const int MaximumSpans = 4_000;

    /// <summary>The most density columns one read cuts an interval into.</summary>
    public const int MaximumDensityColumns = 2_048;

    /// <summary>
    /// The calls of one channel that run within <paramref name="interval"/> (workspace ticks), in reading order, when there
    /// are at most <paramref name="budget"/> of them; when there are more, none of them and their density in
    /// <paramref name="columns"/> equal columns instead, as §6.2 falls back from marks to density rather than drop marks.
    /// A call runs within the interval when it starts before the interval ends and has not stopped before it begins; a call
    /// still open at capture end runs to the end of the session.
    /// </summary>
    public static RpcCallSpanPage Spans(
        SessionStore store,
        string channelKey,
        TimeRange interval,
        int budget = MaximumSpans,
        int columns = 512,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (budget is < 1 or > MaximumSpans) throw new ArgumentOutOfRangeException(nameof(budget));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (columns is < 1 or > MaximumDensityColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        if (!RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface))
        {
            throw new ArgumentException("This key names no RPC channel.", nameof(channelKey));
        }

        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, RpcCallIndex? calls) = Derive(store, lease, cancellationToken);
        RpcCallGroup? group = calls?.GroupOf(instance, side, rpcInterface);
        if (calls is null || group is null || !group.Process.IsAdmittedUnder(policy))
        {
            return new(manifest.SessionId, manifest.Generation, interval, [], 0,
                "This generation holds no call of this channel under the evidence policy.");
        }

        var drawn = new List<RpcCallSpanView>(Math.Min(budget, (int)Math.Min(group.Counts.Calls, int.MaxValue)));
        long total = 0;

        // Every call in the interval is counted into the columns as it is read, so an interval denser than the budget
        // costs one pass and never holds more than the budget's calls. A column is at least one tick wide.
        int width = (int)Math.Min(columns, interval.SpanTicks);
        long[] running = new long[width + 1];
        long[] failed = new long[width + 1];
        foreach (RpcCallSpan span in calls.SpansOf(group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? start = span.StartNanoseconds / 100;
            long? stop = span.StopNanoseconds / 100;
            long first = start ?? stop!.Value;
            long last = stop ?? (span.State == RpcCallState.OpenAtCaptureEnd ? long.MaxValue : first);
            if (first >= interval.EndTicks || last < interval.StartTicks)
            {
                continue;
            }

            total++;
            int from = RpcCallDensity.ColumnOf(interval, width, first);
            int to = RpcCallDensity.ColumnOf(interval, width, last) + 1;
            running[from]++;
            running[to]--;
            if (span.State == RpcCallState.Completed && span.Status is { } code && code != 0)
            {
                failed[from]++;
                failed[to]--;
            }

            if (drawn.Count < budget)
            {
                drawn.Add(new(
                    RpcChannelKeys.Call(channelKey, span.Stream, span.Epoch, span.Ordinal, span.FactKey),
                    start,
                    stop,
                    span.State,
                    span.Status,
                    span.Procedure));
            }
        }

        if (total <= budget)
        {
            return new(manifest.SessionId, manifest.Generation, interval, drawn, total, null);
        }

        for (int column = 1; column < width; column++)
        {
            running[column] += running[column - 1];
            failed[column] += failed[column - 1];
        }

        return new(manifest.SessionId, manifest.Generation, interval, [], total, null)
        {
            Density = new(interval, running[..width], failed[..width]),
        };
    }

    /// <summary>
    /// The RPC channels of one process instance, each one side's calls to one interface, bound as strongly as
    /// <paramref name="policy"/> admits.
    /// </summary>
    /// <remarks>
    /// Within <paramref name="interval"/>, in 100-nanosecond presentation ticks, a channel counts the calls the interval
    /// holds by the record that counts each (<see cref="CountedWithin"/>), and its call records by their own reading; a
    /// channel with no call in it stays, counting none, as a paired channel does.
    /// </remarks>
    public static RpcChannelList Channels(
        SessionStore store,
        ProcessInstanceId instance,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, RpcCallIndex? calls) = Derive(store, lease, cancellationToken);
        if (calls is null)
        {
            return new(manifest.SessionId, manifest.Generation, []);
        }

        TimeRange? native = Native(store, manifest, interval);
        RpcPeerIndex peers = Peers(store, manifest, Segments(store, manifest, calls), cancellationToken);
        return new(manifest.SessionId, manifest.Generation,
        [
            .. calls.Groups
                .Where(group => group.Process.IsAdmittedUnder(policy) && calls.Processes.Instances[group.Process.Instance].Id == instance)
                .Select(group => interval is null
                    ? Summary(calls, peers, group, instance, policy)
                    : ScopedSummary(calls, peers, group, instance, native, policy))
                .OrderByDescending(channel => channel.Records)
                .ThenBy(channel => channel.Key, StringComparer.Ordinal),
        ]);
    }

    /// <summary>
    /// Whether a call is in <paramref name="native"/> by the record that counts it (`metrics-v1` §8a): a completed call and
    /// a stop whose start is not in the evidence by the stop, a call open at capture end by its start, and a record that
    /// pairs with nothing by itself. Every call is so in exactly one place in time.
    /// </summary>
    public static bool CountedWithin(RpcCallOutcome call, TimeRange native)
    {
        RpcCallMark? counted = call.State switch
        {
            RpcCallState.Completed or RpcCallState.StartNotObserved => call.Stop,
            RpcCallState.OpenAtCaptureEnd => call.Start,
            _ => call.Stop ?? call.Start,
        };
        return counted is { } mark && native.Contains(mark.NativeTicks);
    }

    /// <summary>
    /// One channel's calls in reading order, from <paramref name="offset"/>, at most <paramref name="pageSize"/> of them.
    /// </summary>
    /// <remarks>
    /// Within <paramref name="interval"/>, in 100-nanosecond presentation ticks, the page holds only the calls the interval
    /// holds by the record that counts each, in the same order, so the channel's count and its rows agree.
    /// </remarks>
    public static RpcCallPage Calls(
        SessionStore store,
        string channelKey,
        int offset = 0,
        int pageSize = DefaultPageSize,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!RpcChannelKeys.TryParseChannel(channelKey, out _, out _, out _))
        {
            throw new ArgumentException("This key names no RPC channel.", nameof(channelKey));
        }

        return Page(store, channelKey, offset, (_, _, _) => pageSize, interval, policy, cancellationToken);
    }

    /// <summary>
    /// One channel's calls in reading order from the first, through the call <paramref name="callKey"/> names and to the
    /// end of the page of <paramref name="pageSize"/> that holds it, so a list the user returns to lands on the call they
    /// left it for, however far down its pages (§3.2). A call the scope does not list, or one past the first
    /// <paramref name="maximum"/> calls it lists, reads the first page alone, as <see cref="Calls"/> does.
    /// </summary>
    public static RpcCallPage CallsThrough(
        SessionStore store,
        string callKey,
        int pageSize = DefaultPageSize,
        int maximum = MaximumListedThrough,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maximum is < 1 or > MaximumListedThrough) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!RpcChannelKeys.TryParseCall(callKey, out string channelKey, out var first))
        {
            throw new ArgumentException("This key names no RPC call.", nameof(callKey));
        }

        return Page(store, channelKey, 0, (calls, group, held) =>
        {
            // Where the call is listed: its place in the channel, or among the calls the interval holds.
            int? position = calls.PositionOf(group, first.Stream, first.Epoch, first.Ordinal, first.FactKey);
            int? listed = position is not { } place ? null
                : held is null ? place
                : Array.BinarySearch(held, place) is var index and >= 0 ? index : null;
            return listed is { } at && at < maximum ? (int)Math.Min(((long)at / pageSize + 1) * pageSize, maximum) : pageSize;
        }, interval, policy, cancellationToken);
    }

    /// <summary>
    /// The calls of one channel from <paramref name="offset"/> in the order a scope lists them - every call, or those
    /// <paramref name="interval"/> holds - as many as <paramref name="count"/> says for that listing: given the index, the
    /// channel's group and, within an interval, the ascending positions it holds.
    /// </summary>
    private static RpcCallPage Page(
        SessionStore store,
        string channelKey,
        int offset,
        Func<RpcCallIndex, RpcCallGroup, int[]?, int> count,
        TimeRange? interval,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        _ = RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface);
        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, RpcCallIndex? calls) = Derive(store, lease, cancellationToken);
        RpcCallGroup? group = calls?.GroupOf(instance, side, rpcInterface);
        if (calls is null || group is null || !group.Process.IsAdmittedUnder(policy))
        {
            return new(manifest.SessionId, manifest.Generation, null, offset, [], false,
                "This generation holds no call of this channel under the evidence policy. Return to the process and "
                + "select its channel again.");
        }

        SegmentReaderV1[] segments = Segments(store, manifest, calls);
        RpcPeerIndex peers = Peers(store, manifest, segments, cancellationToken);
        if (interval is not null)
        {
            TimeRange? native = Native(store, manifest, interval);
            int[] held = native is { } range
                ? [.. calls.OutcomesOf(group).Select((call, position) => (call, position))
                    .Where(entry => CountedWithin(entry.call, range)).Select(entry => entry.position)]
                : [];
            int[] shown = [.. held.Skip(offset).Take(count(calls, group, held))];
            IReadOnlyList<RpcCall> scoped = calls.CallsAt(group, segments, shown);
            return new(
                manifest.SessionId,
                manifest.Generation,
                ScopedSummary(calls, peers, group, instance, native, policy),
                offset,
                [.. scoped.Select((call, index) => Row(channelKey, call, calls, peers, group, shown[index], segments, policy))],
                (long)offset + scoped.Count < held.Length,
                null);
        }

        IReadOnlyList<RpcCall> page = calls.CallsOf(group, segments, offset, count(calls, group, null));
        return new(
            manifest.SessionId,
            manifest.Generation,
            Summary(calls, peers, group, instance, policy),
            offset,
            [.. page.Select((call, index) => Row(channelKey, call, calls, peers, group, offset + index, segments, policy))],
            (long)offset + page.Count < group.Counts.Calls,
            null);
    }

    /// <summary>One call's row, with its other end when the capture collected ALPC.</summary>
    private static RpcCallRow Row(
        string channelKey,
        RpcCall call,
        RpcCallIndex calls,
        RpcPeerIndex peers,
        RpcCallGroup group,
        int position,
        IReadOnlyList<SegmentReaderV1> segments,
        EvidencePolicy policy)
    {
        var row = new RpcCallRow(CallKey(channelKey, call), call);
        if (!peers.CollectedAlpc)
        {
            return row;
        }

        (RpcPeerState state, RpcCall? other) = peers.PeerOf(group, position, segments);
        if (other is null)
        {
            return state == default ? row : row with { OtherEnd = new(state, null, null) };
        }

        // The other call's key names it on its own channel, so a reader can open the call at the other end.
        RpcCallGroup otherGroup = peers.OtherCallOf(group, position)!.Value.Group;
        RpcChannelPeer process = Peer(calls, other.ProcessId, other.Process, otherGroup.Interface, 1, policy);
        string? key = process.Instance is { } instance
            ? CallKey(RpcChannelKeys.Channel(instance, otherGroup.Side, otherGroup.Interface), other)
            : null;
        return row with
        {
            OtherEnd = new(null, process, key) { FirstNanoseconds = (other.Start ?? other.Stop)?.SessionRelativeTicks },
        };
    }

    /// <summary>Who is at the other end of a channel's calls, or of those at <paramref name="positions"/>.</summary>
    private static RpcChannelPeers? PeersOf(
        RpcCallIndex calls,
        RpcPeerIndex peers,
        RpcCallGroup group,
        IReadOnlyList<int>? positions,
        EvidencePolicy policy)
    {
        if (!peers.CollectedAlpc)
        {
            return null;
        }

        RpcPeerCounts counts = peers.CountsOf(group, positions);
        IReadOnlyList<RpcPeerTally> tallies = peers.PeersOf(group, positions);
        return new(
            group.Side == RpcCallSide.Client ? counts.Served : tallies.Sum(tally => tally.Calls),
            counts.Unresolved,
            [.. tallies.Select(tally => Peer(calls, tally.ProcessId, tally.Process, tally.Interface, tally.Calls, policy))]);
    }

    /// <summary>A process at the other end, named by its instance where the evidence policy admits its binding.</summary>
    private static RpcChannelPeer Peer(
        RpcCallIndex calls,
        int processId,
        ProcessBinding binding,
        Guid? rpcInterface,
        long count,
        EvidencePolicy policy)
    {
        ProcessInstance? instance = binding.IsAdmittedUnder(policy) ? calls.Processes.Instances[binding.Instance] : null;
        return new(processId, instance?.Id, instance?.ImageName, rpcInterface, count);
    }

    /// <summary>The key of one call on a channel: its first record's raw locator and fact key.</summary>
    public static string CallKey(string channelKey, RpcCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        RawRecordId first = call.Identity.RawRecordId;
        return RpcChannelKeys.Call(channelKey, first.StreamId, first.SourceEpoch, first.RecordOrdinal, call.Identity.FactKey);
    }

    /// <summary>
    /// The records an RPC channel, call or relationship key scopes, by segment name and row in the leased generation, for
    /// an evidence page; null when the generation holds no such channel, call or relationship under the policy.
    /// </summary>
    internal static HashSet<(string Segment, int Row)>? RecordsOf(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        string operationKey,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        // An RPC relationship's key opens the calls its links join, at both ends and in either direction (R4); one no
        // admitted link joins is no relationship of this generation under the policy.
        if (RpcPeerEdges.TryParseKey(operationKey, out ProcessInstanceId one, out ProcessInstanceId other))
        {
            RpcPeerIndex linked = Peers(store, manifest, segments, cancellationToken);
            IReadOnlyList<ProcessInstance> instances = linked.Calls.Processes.Instances;
            HashSet<(string Segment, int Row)> joined = [.. linked.LinkedRecords(link => RpcPeerEdges.Joins(link, instances, one, other, policy))
                .Select(record => (linked.Calls.SegmentNames[record.Segment] ?? string.Empty, record.Row))];
            return joined.Count == 0 ? null : joined;
        }

        string channelKey = operationKey;
        (uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey)? first = null;
        if (RpcChannelKeys.TryParseCall(operationKey, out string callChannel, out var callFirst))
        {
            channelKey = callChannel;
            first = callFirst;
        }

        if (!RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface))
        {
            throw new ArgumentException("This key names no RPC channel, call or relationship.", nameof(operationKey));
        }

        RpcCallIndex calls = Index(store, manifest, segments, cancellationToken);
        if (calls.GroupOf(instance, side, rpcInterface) is not { } group || !group.Process.IsAdmittedUnder(policy))
        {
            return null;
        }

        int? position = null;
        if (first is { } call)
        {
            position = calls.PositionOf(group, call.Stream, call.Epoch, call.Ordinal, call.FactKey);
            if (position is null)
            {
                return null;
            }
        }

        return [.. calls.RecordsOf(group, position).Select(record => (calls.SegmentNames[record.Segment] ?? string.Empty, record.Row))];
    }

    /// <summary>
    /// A channel's summary within an interval: the calls it holds by the record that counts each, their durations when
    /// completed, and the call records read within it. An interval no reading falls in holds none of either.
    /// </summary>
    private static RpcChannelSummary ScopedSummary(
        RpcCallIndex calls,
        RpcPeerIndex peers,
        RpcCallGroup group,
        ProcessInstanceId instance,
        TimeRange? native,
        EvidencePolicy policy)
    {
        long all = 0, started = 0, completed = 0, failed = 0, open = 0, notObserved = 0, noActivity = 0, ambiguous = 0, records = 0;
        var durations = new List<long>();
        var held = new List<int>();
        int position = -1;
        using IEnumerator<RpcCallSpan> spans = calls.SpansOf(group).GetEnumerator();
        foreach (RpcCallOutcome call in calls.OutcomesOf(group))
        {
            spans.MoveNext();
            position++;
            if (native is not { } range) continue;
            records += (call.Start is { } start && range.Contains(start.NativeTicks) ? 1 : 0)
                + (call.Stop is { } stop && range.Contains(stop.NativeTicks) ? 1 : 0);
            if (!CountedWithin(call, range)) continue;
            held.Add(position);
            all++;
            started += call.Start is null ? 0 : 1;
            switch (call.State)
            {
                case RpcCallState.Completed:
                    completed++;
                    failed += call.Status is { } status && status != 0 ? 1 : 0;
                    if (spans.Current is { StartNanoseconds: { } from, StopNanoseconds: { } to }) durations.Add(to - from);
                    break;
                case RpcCallState.OpenAtCaptureEnd:
                    open++;
                    break;
                case RpcCallState.StartNotObserved:
                    notObserved++;
                    break;
                case RpcCallState.NoActivityId:
                    noActivity++;
                    break;
                default:
                    ambiguous++;
                    break;
            }
        }

        return new(
            RpcChannelKeys.Channel(instance, group.Side, group.Interface),
            instance,
            group.Side,
            group.Interface,
            new RpcCallCounts
            {
                Calls = all,
                Started = started,
                Completed = completed,
                Failed = failed,
                OpenAtCaptureEnd = open,
                StartNotObserved = notObserved,
                NoActivityId = noActivity,
                Ambiguous = ambiguous,
            },
            RpcCallDurations.Of(durations),
            records)
        {
            Peers = PeersOf(calls, peers, group, held, policy),
        };
    }

    /// <summary>A presentation interval on the generation's source clock; null when it names none or no reading can fall in it.</summary>
    private static TimeRange? Native(SessionStore store, SessionManifestV1 manifest, TimeRange? interval) =>
        interval is { } presentation && SessionSegments.SourceClock(store.Root, manifest) is { } clock
            ? RankingScope.NativeInterval(clock, presentation)
            : null;

    private static RpcChannelSummary Summary(
        RpcCallIndex calls,
        RpcPeerIndex peers,
        RpcCallGroup group,
        ProcessInstanceId instance,
        EvidencePolicy policy) => new(
        RpcChannelKeys.Channel(instance, group.Side, group.Interface),
        instance,
        group.Side,
        group.Interface,
        group.Counts,
        group.Durations,
        calls.RecordsOf(group).LongCount())
    {
        Peers = PeersOf(calls, peers, group, null, policy),
    };

    private static (SessionManifestV1 Manifest, RpcCallIndex? Calls) Derive(
        SessionStore store,
        EvidenceLease lease,
        CancellationToken cancellationToken)
    {
        SessionManifestV1 manifest = lease.Manifest;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return segments.Length == 0 || SessionSegments.SourceClock(store.Root, manifest) is null
            ? (manifest, null)
            : (manifest, Index(store, manifest, segments, cancellationToken));
    }

    private static RpcCallIndex Index(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        CancellationToken cancellationToken)
    {
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation has no source clock for process binding.");
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return SessionDerivationCache.For(manifest).RpcCalls(store.Root, segments, clock, fields, cancellationToken);
    }

    /// <summary>The generation's call other ends, followed once over its calls from the same segments.</summary>
    private static RpcPeerIndex Peers(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        CancellationToken cancellationToken)
    {
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation has no source clock for process binding.");
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return SessionDerivationCache.For(manifest).RpcPeers(store.Root, segments, clock, fields, cancellationToken);
    }

    /// <summary>The generation's segments in the order the calls were paired from.</summary>
    private static SegmentReaderV1[] Segments(SessionStore store, SessionManifestV1 manifest, RpcCallIndex calls) =>
        [.. calls.SegmentNames.Select(name => SessionSegments.Open(store, manifest, name
            ?? throw new InvalidDataException("A segment the calls were paired from has no published name.")))];
}
