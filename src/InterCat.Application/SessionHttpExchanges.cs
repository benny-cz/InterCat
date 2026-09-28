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
/// Reads a published session's HTTP exchanges for the ladder (ADR-037, M8): a process's exchanges as one channel, and its
/// exchanges a page at a time. Each read leases the current generation, whose exchanges are grouped once and shared by
/// every read of it.
/// </summary>
public static class SessionHttpExchanges
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 500;

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

    private static (SessionManifestV1 Manifest, HttpExchangeIndex? Index) Derive(
        SessionStore store,
        EvidenceLease lease,
        CancellationToken cancellationToken)
    {
        SessionManifestV1 manifest = lease.Manifest;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return (manifest, segments.Length == 0 ? null : Index(store, manifest, segments, cancellationToken));
    }

    private static HttpExchangeIndex? Index(
        SessionStore store,
        SessionManifestV1 manifest,
        IReadOnlyList<SegmentReaderV1> segments,
        CancellationToken cancellationToken)
    {
        if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
        {
            return null;
        }

        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return SessionDerivationCache.For(manifest).HttpExchanges(store.Root, segments, clock, fields, cancellationToken);
    }
}
