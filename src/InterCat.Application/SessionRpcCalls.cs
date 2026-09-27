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
public sealed record RpcCallRow(string Key, RpcCall Call);

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
    public TimeRange ColumnInterval(int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns);
        return new(Boundary(Interval, Columns, column), Boundary(Interval, Columns, column + 1));
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
    public static RpcChannelList Channels(
        SessionStore store,
        ProcessInstanceId instance,
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

        return new(manifest.SessionId, manifest.Generation,
        [
            .. calls.Groups
                .Where(group => group.Process.IsAdmittedUnder(policy) && calls.Processes.Instances[group.Process.Instance].Id == instance)
                .Select(group => Summary(calls, group, instance))
                .OrderByDescending(channel => channel.Records)
                .ThenBy(channel => channel.Key, StringComparer.Ordinal),
        ]);
    }

    /// <summary>
    /// One channel's calls in reading order, from <paramref name="offset"/>, at most <paramref name="pageSize"/> of them.
    /// </summary>
    public static RpcCallPage Calls(
        SessionStore store,
        string channelKey,
        int offset = 0,
        int pageSize = DefaultPageSize,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface))
        {
            throw new ArgumentException("This key names no RPC channel.", nameof(channelKey));
        }

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
        IReadOnlyList<RpcCall> page = calls.CallsOf(group, segments, offset, pageSize);
        return new(
            manifest.SessionId,
            manifest.Generation,
            Summary(calls, group, instance),
            offset,
            [.. page.Select(call => new RpcCallRow(CallKey(channelKey, call), call))],
            (long)offset + page.Count < group.Counts.Calls,
            null);
    }

    /// <summary>The key of one call on a channel: its first record's raw locator and fact key.</summary>
    public static string CallKey(string channelKey, RpcCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        RawRecordId first = call.Identity.RawRecordId;
        return RpcChannelKeys.Call(channelKey, first.StreamId, first.SourceEpoch, first.RecordOrdinal, call.Identity.FactKey);
    }

    /// <summary>
    /// The records an RPC channel or call key scopes, by segment name and row in the leased generation, for an evidence
    /// page; null when the generation holds no such channel or call.
    /// </summary>
    internal static HashSet<(string Segment, int Row)>? RecordsOf(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        string rpcKey,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        string channelKey = rpcKey;
        (uint Stream, uint Epoch, ulong Ordinal, FactKey FactKey)? first = null;
        if (RpcChannelKeys.TryParseCall(rpcKey, out string callChannel, out var callFirst))
        {
            channelKey = callChannel;
            first = callFirst;
        }

        if (!RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface))
        {
            throw new ArgumentException("This key names no RPC channel or call.", nameof(rpcKey));
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

    private static RpcChannelSummary Summary(RpcCallIndex calls, RpcCallGroup group, ProcessInstanceId instance) => new(
        RpcChannelKeys.Channel(instance, group.Side, group.Interface),
        instance,
        group.Side,
        group.Interface,
        group.Counts,
        group.Durations,
        calls.RecordsOf(group).LongCount());

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

    /// <summary>The generation's segments in the order the calls were paired from.</summary>
    private static SegmentReaderV1[] Segments(SessionStore store, SessionManifestV1 manifest, RpcCallIndex calls) =>
        [.. calls.SegmentNames.Select(name => SessionSegments.Open(store, manifest, name
            ?? throw new InvalidDataException("A segment the calls were paired from has no published name.")))];
}
