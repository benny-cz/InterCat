using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Keys for a process's HTTP exchanges on the ladder and for one exchange. They name an instance and the exchange's first
/// buffer's record, never a position, so a key reads the same exchange in every generation that still holds it.
/// </summary>
public static class HttpExchangeKeys
{
    public const string Prefix = "http:";

    /// <summary>The key of one process instance's HTTP exchanges.</summary>
    public static string Channel(ProcessInstanceId instance) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{instance.Value:N}");

    /// <summary>The key of one exchange, by its first buffer's raw locator.</summary>
    public static string Exchange(string channelKey, uint stream, uint epoch, ulong ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"{channelKey}/{stream}.{epoch}.{ordinal}");

    /// <summary>Whether a key names a process's HTTP exchanges or one of them.</summary>
    public static bool IsHttp(string? key) => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The instance a channel key names; false for any other key.</summary>
    public static bool TryParseChannel(string? key, out ProcessInstanceId instance)
    {
        instance = default;
        if (!IsHttp(key) || key!.Contains('/', StringComparison.Ordinal)
            || !Guid.TryParseExact(key.AsSpan(Prefix.Length), "N", out Guid id) || id == Guid.Empty)
        {
            return false;
        }

        instance = new(id);
        return true;
    }

    /// <summary>The channel and the first buffer's record an exchange key names; false for any other key.</summary>
    public static bool TryParseExchange(string? key, out string channelKey, out (uint Stream, uint Epoch, ulong Ordinal) first)
    {
        channelKey = string.Empty;
        first = default;
        int slash = key?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (slash < 0 || !TryParseChannel(key![..slash], out _))
        {
            return false;
        }

        string[] parts = key[(slash + 1)..].Split('.');
        if (parts.Length != 3
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint stream)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint epoch)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ordinal))
        {
            return false;
        }

        channelKey = key[..slash];
        first = (stream, epoch, ordinal);
        return true;
    }
}

/// <summary>
/// One process instance's HTTP exchanges as the ladder shows them (ADR-037, M8): how many, how many were recorded whole,
/// the bytes their requests and responses measured, and how long an exchange took, from what its records' metadata and
/// source fields say - never from their content.
/// </summary>
public sealed record HttpChannelSummary(
    string Key,
    ProcessInstanceId Instance,
    long Exchanges,
    long Complete,
    long Records,
    long RequestBytes,
    long ResponseBytes,
    long? MedianNanoseconds)
{
    /// <summary>The channel in words.</summary>
    public static string Name => "HTTP exchanges through WinINet";

    /// <summary>What its exchanges came to, in one line: how many, how many not recorded whole, and the median duration.</summary>
    public string Outcome(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        var parts = new List<string> { string.Create(format, $"{Exchanges:N0} {(Exchanges == 1 ? "exchange" : "exchanges")}") };
        if (Complete < Exchanges) parts.Add(string.Create(format, $"{Exchanges - Complete:N0} not recorded whole"));
        if (MedianNanoseconds is { } median) parts.Add("median " + OperationText.Duration(median, format));
        return string.Join(" · ", parts);
    }
}

/// <summary>A process instance's HTTP exchanges in one generation: none, or its one channel of them.</summary>
public sealed record HttpChannelList(Guid SessionId, long Generation, IReadOnlyList<HttpChannelSummary> Channels);

/// <summary>One exchange, with the key that names it in every generation holding it.</summary>
public sealed record HttpExchangeRow(string Key, HttpExchange Exchange)
{
    /// <summary>
    /// Its parts in one phrase - "request 181 B · response 115 B + 98,304 B" - and, when one was not recorded whole,
    /// which.
    /// </summary>
    public string Parts(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        string request = Exchange.RequestBody.Recorded
            ? string.Create(format, $"request {Exchange.RequestHead.Bytes:N0} B + {Exchange.RequestBody.Bytes:N0} B")
            : string.Create(format, $"request {Exchange.RequestHead.Bytes:N0} B");
        string response = string.Create(format, $"response {Exchange.ResponseHead.Bytes:N0} B + {Exchange.ResponseBody.Bytes:N0} B");
        string[] missing =
        [
            .. Enum.GetValues<HttpPart>()
                .Where(part => part != HttpPart.RequestBody || Exchange.RequestBody.Recorded)
                .Where(part => !Exchange.Part(part).Whole)
                .Select(PartName),
        ];
        return missing.Length == 0
            ? $"{request} · {response}"
            : $"{request} · {response} · not recorded whole: {string.Join(", ", missing)}";
    }

    /// <summary>A part in words, as a part's statement names it.</summary>
    public static string PartName(HttpPart part) => part switch
    {
        HttpPart.RequestHead => "request head",
        HttpPart.RequestBody => "request body",
        HttpPart.ResponseHead => "response head",
        HttpPart.ResponseBody => "response body",
        _ => "part",
    };
}

/// <summary>
/// A page of one process's exchanges in reading order. A channel this generation no longer holds is a problem stated, not
/// an empty page.
/// </summary>
public sealed record HttpExchangePage(
    Guid SessionId,
    long Generation,
    HttpChannelSummary? Channel,
    int Offset,
    IReadOnlyList<HttpExchangeRow> Exchanges,
    bool More,
    string? Problem);

/// <summary>
/// One exchange as the timeline draws it (§6.2): from its first recorded buffer to the buffer that ended its response, in
/// workspace ticks, or to its last recorded buffer when its response's end was not recorded. Whether it was recorded whole
/// (<see cref="HttpExchange.Complete"/>) says how it is drawn; its number and the bytes of its two messages say what it was.
/// </summary>
public sealed record HttpExchangeSpanView(
    string Key,
    long FirstTicks,
    long LastTicks,
    bool Ended,
    bool Complete,
    long Number,
    long RequestBytes,
    long ResponseBytes);

/// <summary>
/// A process's HTTP exchanges within one interval as §6.2's density regime draws them, when there are more than a lane draws
/// one by one: the interval cut into equal columns, and in each the exchanges running in it and how many of those were not
/// recorded whole. An exchange runs in every column from its first buffer's to its end's, as a call runs from its start to
/// its stop; it is an overlap count, never a count of records.
/// </summary>
public sealed record HttpExchangeDensity(TimeRange Interval, IReadOnlyList<long> Running, IReadOnlyList<long> Incomplete)
{
    /// <summary>The most exchanges running in any one column: what the lane's intensity scale reaches.</summary>
    public long Maximum { get; } = Running.Count == 0 ? 0 : Running.Max();

    public int Columns => Running.Count;

    /// <summary>The interval one column covers, as an RPC channel's density columns divide theirs.</summary>
    public TimeRange ColumnInterval(int column) => RpcCallDensity.ColumnIntervalOf(Interval, Columns, column);
}

/// <summary>
/// A process's HTTP exchanges within one interval, in reading order, when they are no more than a lane draws one by one;
/// otherwise none of them, and their <see cref="Density"/> instead (§6.2). <see cref="Total"/> counts every exchange the
/// interval holds; one whose first buffer has no session time is not placed, and is in neither.
/// </summary>
public sealed record HttpExchangeSpanPage(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyList<HttpExchangeSpanView> Exchanges,
    long Total,
    string? Problem)
{
    /// <summary>The exchanges as density columns, when there were more of them than the budget; null otherwise.</summary>
    public HttpExchangeDensity? Density { get; init; }
}

/// <summary>
/// Reads a published session's HTTP exchanges for the ladder (ADR-037, M8): a process's exchanges as one channel, and its
/// exchanges a page at a time. Each read leases the current generation, whose exchanges are grouped once and shared by
/// every read of it.
/// </summary>
public static class SessionHttpExchanges
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 500;

    /// <summary>The most exchanges one timeline read returns one by one; a denser interval is read as density columns.</summary>
    public const int MaximumSpans = SessionRpcCalls.MaximumSpans;

    /// <summary>The most density columns one read cuts an interval into.</summary>
    public const int MaximumDensityColumns = SessionRpcCalls.MaximumDensityColumns;

    /// <summary>
    /// The exchanges <paramref name="channelKey"/> names that run within <paramref name="interval"/> (workspace ticks), in
    /// reading order, when there are at most <paramref name="budget"/> of them; when there are more, none of them and their
    /// density in <paramref name="columns"/> equal columns instead, as an RPC channel's calls are read for its lane.
    /// </summary>
    public static HttpExchangeSpanPage Spans(
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
        if (columns is < 1 or > MaximumDensityColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!HttpExchangeKeys.TryParseChannel(channelKey, out ProcessInstanceId instance))
        {
            throw new ArgumentException("This key names no process's HTTP exchanges.", nameof(channelKey));
        }

        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, HttpExchangeIndex? index) = Derive(store, lease, cancellationToken);
        if (index?.GroupOf(instance) is not { } group || !group.Process.IsAdmittedUnder(policy))
        {
            return new(manifest.SessionId, manifest.Generation, interval, [], 0,
                "This generation holds no HTTP exchanges of that process under the evidence policy.");
        }

        // Every exchange in the interval is counted into the columns as it is read, so an interval denser than the budget
        // costs one pass and never holds more than the budget's exchanges. A column is at least one tick wide.
        var drawn = new List<HttpExchangeSpanView>(Math.Min(budget, group.Exchanges));
        long total = 0;
        int width = (int)Math.Min(columns, interval.SpanTicks);
        long[] running = new long[width + 1];
        long[] incomplete = new long[width + 1];
        foreach (HttpExchange exchange in index.ExchangesOf(group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Placed(exchange) is not (long first, long last) || first >= interval.EndTicks || last < interval.StartTicks)
            {
                continue;
            }

            total++;
            int from = RpcCallDensity.ColumnOf(interval, width, first);
            int to = RpcCallDensity.ColumnOf(interval, width, last) + 1;
            running[from]++;
            running[to]--;
            if (!exchange.Complete)
            {
                incomplete[from]++;
                incomplete[to]--;
            }

            if (drawn.Count < budget)
            {
                drawn.Add(new(
                    HttpExchangeKeys.Exchange(channelKey, exchange.First.Stream, exchange.First.Epoch, exchange.First.Ordinal),
                    first,
                    last,
                    exchange.Ended,
                    exchange.Complete,
                    exchange.Number,
                    exchange.RequestBytes,
                    exchange.ResponseBytes));
            }
        }

        if (total <= budget)
        {
            return new(manifest.SessionId, manifest.Generation, interval, drawn, total, null);
        }

        for (int column = 1; column < width; column++)
        {
            running[column] += running[column - 1];
            incomplete[column] += incomplete[column - 1];
        }

        return new(manifest.SessionId, manifest.Generation, interval, [], total, null)
        {
            Density = new(interval, running[..width], incomplete[..width]),
        };
    }

    /// <summary>
    /// Where an exchange lies in workspace ticks: from its first buffer to the one that ended its response, or to its last
    /// buffer when its response's end was not recorded. Null when its first buffer has no session time to place it by.
    /// </summary>
    private static (long First, long Last)? Placed(HttpExchange exchange)
    {
        if (exchange.FirstNanoseconds is not { } firstNanoseconds)
        {
            return null;
        }

        long end = (exchange.Ended ? exchange.ResponseEndNanoseconds : null) ?? exchange.LastNanoseconds ?? firstNanoseconds;
        return (firstNanoseconds / 100, Math.Max(firstNanoseconds, end) / 100);
    }

    /// <summary>
    /// The HTTP exchanges of <paramref name="instance"/>, as one channel, or none when it made none. Within
    /// <paramref name="interval"/> (workspace ticks) an exchange counts where its last recorded buffer falls, so each
    /// exchange is counted in exactly one place in time.
    /// </summary>
    public static HttpChannelList Channels(
        SessionStore store,
        ProcessInstanceId instance,
        TimeRange? interval = null,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, HttpExchangeIndex? index) = Derive(store, lease, cancellationToken);
        if (index?.GroupOf(instance) is not { } group || !group.Process.IsAdmittedUnder(policy))
        {
            return new(manifest.SessionId, manifest.Generation, []);
        }

        return new(manifest.SessionId, manifest.Generation, [Summary(index, group, instance, Native(store, manifest, interval), interval is not null)]);
    }

    /// <summary>
    /// A page of the exchanges <paramref name="channelKey"/> names, from <paramref name="offset"/> in reading order,
    /// counted within <paramref name="interval"/> when one is given.
    /// </summary>
    public static HttpExchangePage Exchanges(
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
        if (!HttpExchangeKeys.TryParseChannel(channelKey, out ProcessInstanceId instance))
        {
            throw new ArgumentException("This key names no process's HTTP exchanges.", nameof(channelKey));
        }

        using EvidenceLease lease = store.AcquireLease();
        (SessionManifestV1 manifest, HttpExchangeIndex? index) = Derive(store, lease, cancellationToken);
        if (index?.GroupOf(instance) is not { } group || !group.Process.IsAdmittedUnder(policy))
        {
            return new(manifest.SessionId, manifest.Generation, null, offset, [], false,
                "This generation holds no HTTP exchanges of that process.");
        }

        TimeRange? native = Native(store, manifest, interval);
        IEnumerable<HttpExchange> scoped = index.ExchangesOf(group);
        if (interval is not null)
        {
            scoped = native is { } range ? scoped.Where(exchange => range.Contains(exchange.LastTicks)) : [];
        }

        HttpExchange[] page = [.. scoped.Skip(offset).Take(pageSize + 1)];
        return new(
            manifest.SessionId,
            manifest.Generation,
            Summary(index, group, instance, native, interval is not null),
            offset,
            [.. page.Take(pageSize).Select(exchange => new HttpExchangeRow(
                HttpExchangeKeys.Exchange(channelKey, exchange.First.Stream, exchange.First.Epoch, exchange.First.Ordinal), exchange))],
            page.Length > pageSize,
            null);
    }

    /// <summary>
    /// The records an HTTP channel or exchange key scopes, by segment name and row in the leased generation, for an evidence
    /// page; null when the generation holds no such channel or exchange.
    /// </summary>
    internal static HashSet<(string Segment, int Row)>? RecordsOf(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        string httpKey,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        string channelKey = httpKey;
        (uint Stream, uint Epoch, ulong Ordinal)? first = null;
        if (HttpExchangeKeys.TryParseExchange(httpKey, out string exchangeChannel, out var exchangeFirst))
        {
            channelKey = exchangeChannel;
            first = exchangeFirst;
        }

        if (!HttpExchangeKeys.TryParseChannel(channelKey, out ProcessInstanceId instance))
        {
            throw new ArgumentException("This key names no process's HTTP exchanges or one of them.", nameof(httpKey));
        }

        if (Index(store, manifest, segments, cancellationToken) is not { } index
            || index.GroupOf(instance) is not { } group || !group.Process.IsAdmittedUnder(policy))
        {
            return null;
        }

        int? position = null;
        if (first is { } exchange)
        {
            position = index.PositionOf(group, exchange.Stream, exchange.Epoch, exchange.Ordinal);
            if (position is null)
            {
                return null;
            }
        }

        return [.. index.RecordsOf(group, position).Select(record => (index.SegmentNames[record.Segment] ?? string.Empty, record.Row))];
    }

    /// <summary>The group's summary, of its exchanges counted within <paramref name="native"/> when <paramref name="scoped"/>.</summary>
    private static HttpChannelSummary Summary(
        HttpExchangeIndex index,
        HttpExchangeGroup group,
        ProcessInstanceId instance,
        TimeRange? native,
        bool scoped)
    {
        IReadOnlyList<HttpExchange> all = index.ExchangesOf(group);
        HttpExchange[] counted = !scoped ? [.. all] : native is { } range ? [.. all.Where(exchange => range.Contains(exchange.LastTicks))] : [];
        long[] durations = [.. counted.Select(exchange => exchange.DurationNanoseconds).OfType<long>().Order()];
        return new(
            HttpExchangeKeys.Channel(instance),
            instance,
            counted.Length,
            counted.Count(exchange => exchange.Complete),
            counted.Sum(exchange => (long)exchange.Records),
            counted.Sum(exchange => exchange.RequestBytes),
            counted.Sum(exchange => exchange.ResponseBytes),
            durations.Length == 0 ? null : durations[(durations.Length - 1) / 2]);
    }

    /// <summary>A presentation interval on the generation's source clock; null when it names none or no reading can fall in it.</summary>
    private static TimeRange? Native(SessionStore store, SessionManifestV1 manifest, TimeRange? interval) =>
        interval is { } presentation && SessionSegments.SourceClock(store.Root, manifest) is { } clock
            ? RankingScope.NativeInterval(clock, presentation)
            : null;

    /// <summary>
    /// The leased generation's exchanges: those the derivation already grouped, read without opening a segment (P25), or
    /// else grouped over every segment.
    /// </summary>
    private static (SessionManifestV1 Manifest, HttpExchangeIndex? Index) Derive(
        SessionStore store,
        EvidenceLease lease,
        CancellationToken cancellationToken)
    {
        SessionManifestV1 manifest = lease.Manifest;
        return SessionSegments.Names(manifest).Count == 0 || SessionSegments.SourceClock(store.Root, manifest) is not { } clock
            ? (manifest, null)
            : (manifest, new GenerationSegments(store, manifest, clock).HttpExchanges(cancellationToken));
    }

    /// <summary>
    /// The generation's exchanges, grouped once over <paramref name="segments"/> and their source fields, each opened only
    /// when the grouping first reads it, and then read without opening one; null when the generation names no source clock.
    /// </summary>
    private static HttpExchangeIndex? Index(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        CancellationToken cancellationToken) =>
        SessionSegments.SourceClock(store.Root, manifest) is not { } clock
            ? null
            : SessionDerivationCache.For(manifest).HttpExchanges(store.Root, segments, clock,
                new SegmentsOnDemand(store, manifest, SessionSegments.FieldNames(manifest)), cancellationToken);
}
