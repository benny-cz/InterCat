using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>
/// A response that redirects (RFC 9110 §15.4): of status 301, 302, 303, 307 or 308, its head naming one Location -
/// <paramref name="Location"/> as the head gave it, and <paramref name="Target"/> and <paramref name="Host"/> as a request
/// made there names them, resolved against its own request's target and host (RFC 3986 §5). Either is null where it cannot
/// be resolved: a location relative to a path, beside a request line not decoded, has no target, and one beside a request
/// that named no host has no host.
/// </summary>
public sealed record HttpRedirect(string Status, string Location, string? Target, string? Host);

/// <summary>The listed exchange that followed a redirect: its key, null where no instance is admitted, and its number.</summary>
public sealed record HttpFollowUp(string? Key, long Number);

/// <summary>
/// Redirects as WinINet follows them (`contracts/http-exchanges-v1.md` §5g, FX-HTTP-009): it makes the request to a
/// redirect's location by itself, and its capture keeps that request as an exchange of its own, numbered apart and begun
/// once the redirect's response ended. No record ties the two, so the exchange that followed a redirect is found by what it
/// asked for: the first listed after it, begun once its response ended, that asked for its location of its host.
/// </summary>
public static class HttpRedirects
{
    /// <summary>What WinINet does with a redirect, as FX-HTTP-009 measured it, said wherever a redirect is shown.</summary>
    public const string Statement = "WinINet follows a redirect by itself, and keeps the request it makes to its location as an "
        + "exchange of its own, numbered apart and begun once the redirect's response ended (FX-HTTP-009): a POST answered 301, "
        + "302 or 303 it asks there again as a GET of no body, and one answered 307 or 308 with the same method and body, but "
        + "only once its client confirms the redirect. No record ties the two exchanges.";

    /// <summary>How a listing names the exchange that followed a redirect, said beside the listing.</summary>
    public const string FollowUpStatement = "A redirect is said to be followed by the first exchange listed after it, begun once "
        + "its response ended, that asked for its location of its host.";

    // A request target resolved against a host its request named none of: it gives the path alone.
    private const string NoHost = "host.invalid";

    /// <summary>
    /// Where <paramref name="heads"/>' response redirects: a response of status 301, 302, 303, 307 or 308 whose head was
    /// decoded to its end and names one Location; null otherwise.
    /// </summary>
    public static HttpRedirect? Of(HttpExchangeHeads heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.StatusLine is not { Status: "301" or "302" or "303" or "307" or "308" } line || heads.Response is not { ReachedEnd: true } response)
        {
            return null;
        }

        string[] locations = [.. response.Fields.Where(field => string.Equals(field.Name, "Location", StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Value)];
        if (locations is not [{ Length: > 0 } location])
        {
            return null;
        }

        // An absolute location names its own host; any other is resolved against its request's target, which a reference
        // to a path needs and one beginning with a slash does not (RFC 3986 §5.2).
        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return new(line.Status, location, PathAndQuery(absolute), absolute.Authority);
        }

        // A request to a proxy names its whole address (FX-HTTP-010), against which any other location resolves, host and all.
        string? host = heads.Host;
        string? target = heads.RequestLine?.Target;
        if (target is not null && Uri.TryCreate(target, UriKind.Absolute, out Uri? whole) && whole.Scheme is "http" or "https")
        {
            return Uri.TryCreate(whole, location, out Uri? onward) && onward.Scheme is "http" or "https"
                ? new(line.Status, location, PathAndQuery(onward), onward.Authority)
                : new(line.Status, location, null, null);
        }

        string from = location.StartsWith('/') ? "/" : target is not null && target.StartsWith('/') ? target : string.Empty;
        if (from.Length == 0 || !Uri.TryCreate("http://" + (host ?? NoHost) + from, UriKind.Absolute, out Uri? origin)
            || !Uri.TryCreate(origin, location, out Uri? resolved) || resolved.Scheme is not ("http" or "https"))
        {
            return new(line.Status, location, null, null);
        }

        bool ownHost = location.StartsWith("//", StringComparison.Ordinal) || host is not null;
        return new(line.Status, location, PathAndQuery(resolved), ownHost ? resolved.Authority : null);
    }

    /// <summary>
    /// For each of <paramref name="exchanges"/> - a process's listed exchanges in reading order, beside their heads where
    /// they were decoded - the position of the listed exchange that followed its redirect: the first after it, begun once
    /// its response ended, whose request asked for its location of its host; -1 where it does not redirect, or none listed
    /// did. Where a host is not known of either, the target alone is matched.
    /// </summary>
    public static int[] FollowUps(IReadOnlyList<HttpExchange> exchanges, IReadOnlyList<HttpExchangeHeads?> heads)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.Count != exchanges.Count)
        {
            throw new ArgumentException("Each listed exchange needs its heads, or null where they were not decoded.", nameof(heads));
        }

        int[] followUps = new int[exchanges.Count];
        Array.Fill(followUps, -1);

        // Each listed exchange by what its request asked for, in reading order.
        var asking = new Dictionary<string, List<(int Position, string? Host)>>(StringComparer.Ordinal);
        for (int position = 0; position < exchanges.Count; position++)
        {
            if (heads[position] is { } asked && Asked(asked) is { } request)
            {
                if (!asking.TryGetValue(request.Target, out List<(int Position, string? Host)>? positions))
                {
                    asking[request.Target] = positions = [];
                }

                positions.Add((position, request.Host));
            }
        }

        for (int position = 0; position < exchanges.Count; position++)
        {
            if (heads[position]?.Redirect is not { Target: { } target } redirect || !asking.TryGetValue(target, out List<(int Position, string? Host)>? candidates))
            {
                continue;
            }

            long? ended = exchanges[position].ResponseEndNanoseconds;
            int first = candidates.BinarySearch((position, null), PositionOrder.Instance);
            for (int at = first < 0 ? ~first : first + 1; at < candidates.Count; at++)
            {
                (int later, string? host) = candidates[at];
                if (redirect.Host is { } wanted && host is { } named && !string.Equals(wanted, named, StringComparison.OrdinalIgnoreCase)) continue;
                if (ended is { } end && exchanges[later].FirstNanoseconds is { } begun && begun < end) continue;
                followUps[position] = later;
                break;
            }
        }

        return followUps;
    }

    /// <summary>
    /// What a request asked for, as a redirect's location is resolved: its target's path and query, and the host it named;
    /// null where its request line was not decoded, or its target is neither a path nor an absolute HTTP address.
    /// </summary>
    private static (string Target, string? Host)? Asked(HttpExchangeHeads heads)
    {
        if (heads.RequestLine?.Target is not { } target)
        {
            return null;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return (PathAndQuery(absolute), absolute.Authority);
        }

        string? host = heads.Host;
        return target.StartsWith('/') && Uri.TryCreate("http://" + (host ?? NoHost) + target, UriKind.Absolute, out Uri? origin)
            ? (PathAndQuery(origin), host is null ? null : origin.Authority)
            : null;
    }

    private static string PathAndQuery(Uri uri) => uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);

    /// <summary>Candidates in reading order, by their position alone.</summary>
    private sealed class PositionOrder : IComparer<(int Position, string? Host)>
    {
        public static readonly PositionOrder Instance = new();

        public int Compare((int Position, string? Host) x, (int Position, string? Host) y) => x.Position.CompareTo(y.Position);
    }
}
