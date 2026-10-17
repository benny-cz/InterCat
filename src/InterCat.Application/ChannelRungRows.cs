using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What an RPC channel's rung and a process's HTTP exchanges' rung list - their calls and exchanges - as an export writes
/// them (§6.4, R18): every one the scope holds, up to a limit, read in one pass of one generation, each at its session time
/// in words that do not follow the reader's culture, so a person's export and <c>icat export</c>'s are the same file.
/// </summary>
public static class ChannelRungRows
{
    private static readonly SessionClock SessionTime = SessionClock.Session(TimeZoneInfo.Utc);

    /// <summary>Whether <paramref name="key"/> names a channel whose rung lists its calls or exchanges.</summary>
    public static bool Lists(string key) => RpcChannelKeys.IsRpc(key) || HttpExchangeKeys.IsHttp(key);

    /// <summary>
    /// The calls or exchanges <paramref name="channelKey"/>'s rung lists within <paramref name="interval"/>, the first
    /// <paramref name="limit"/> of them; <see cref="ChannelRungRead.Complete"/> says whether the limit left any out.
    /// </summary>
    public static ChannelRungRead Read(
        SessionStore store,
        string channelKey,
        TimeRange? interval,
        int limit,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        if (RpcChannelKeys.IsRpc(channelKey))
        {
            RpcCallPage calls = SessionRpcCalls.CallsUpTo(store, channelKey, limit, interval, policy, cancellationToken);
            return new(calls.SessionId, calls.Generation, [.. calls.Calls.Select(RpcCall)], !calls.More, calls.Problem);
        }

        HttpExchangePage exchanges = SessionHttpExchanges.ExchangesUpTo(store, channelKey, limit, interval, policy, cancellationToken);
        return new(exchanges.SessionId, exchanges.Generation, [.. exchanges.Exchanges.Select(HttpExchange)], !exchanges.More,
            exchanges.Problem);
    }

    /// <summary>How many calls or exchanges of <paramref name="channelKey"/>'s rung: "1 call", "2 exchanges".</summary>
    public static string Counted(string channelKey, long count)
    {
        string noun = RpcChannelKeys.IsRpc(channelKey) ? "call" : "exchange";
        return count == 1 ? $"1 {noun}" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}s");
    }

    /// <summary>An export's context once its calls or exchanges are read: incomplete, and saying so, where the limit cut them short.</summary>
    public static ExportContext Describe(ExportContext context, ChannelRungRead read, string incompleteAdvice)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(incompleteAdvice);
        return read.Complete ? context : context with { Complete = false, Caveats = [.. context.Caveats, incompleteAdvice] };
    }

    /// <summary>
    /// One call: when it began, how it went - how long it took, or why that is not known - with its procedure, its status,
    /// and who is at its other end where the capture followed it.
    /// </summary>
    public static LadderRow RpcCall(RpcCallRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RpcCall call = row.Call;
        CultureInfo culture = CultureInfo.InvariantCulture;
        string outcome = (call.State == RpcCallState.Completed
                ? OperationText.Duration(call.DurationNanoseconds!.Value, culture)
                : OperationText.State(call.State))
            + (call.Procedure is { } number ? string.Create(culture, $" · procedure {number:N0}") : string.Empty);
        string status = call.Status is { } code
            ? code == 0 ? "succeeded" : string.Create(culture, $"failed, status {code:N0}")
            : "no status";
        string? otherEnd = row.OtherEnd?.Describe(call.Side);
        return new(row.Key, "RPC call at " + SessionTime.Record((call.Start ?? call.Stop)!.SessionRelativeTicks, culture),
            otherEnd is null ? $"{outcome} · {status}" : $"{outcome} · {status} · {otherEnd}",
            (call.Start is null ? 0 : 1) + (call.Stop is null ? 0 : 1), null, Mechanism.Rpc, CoverageState.UnknownCoverage,
            DetailLevel.Evidence, AccountingSide.CanonicalOwner);
    }

    /// <summary>
    /// One exchange: when it began, how long it took or that its response's end was not recorded, and the bytes recorded of
    /// each of its parts, which are HTTP's messages' and never transport bytes (P3).
    /// </summary>
    public static LadderRow HttpExchange(HttpExchangeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        HttpExchange exchange = row.Exchange;
        CultureInfo culture = CultureInfo.InvariantCulture;
        string took = exchange.DurationNanoseconds is { } duration
            ? OperationText.Duration(duration, culture)
            : "response end not recorded";
        return new(row.Key, "HTTP exchange at " + SessionTime.Record(exchange.FirstNanoseconds, culture),
            string.Create(culture, $"{took} · exchange {exchange.Number:N0} · {row.Parts(culture)}"), exchange.Records, null,
            Mechanism.Http, CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
    }
}

/// <summary>An RPC channel's calls or a process's HTTP exchanges as rows, read from one generation, or why they could not be.</summary>
public sealed record ChannelRungRead(Guid SessionId, long Generation, IReadOnlyList<LadderRow> Rows, bool Complete, string? Problem);
