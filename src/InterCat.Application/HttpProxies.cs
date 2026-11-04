namespace InterCat.Application;

/// <summary>
/// How an exchange's request went to a proxy (`contracts/http-exchanges-v1.md` §5h, FX-HTTP-010): <paramref name="Kind"/>
/// "request" where it names its target's whole address, which a client sends only to a proxy (RFC 9112 §3.2.2), or
/// "tunnel" where it asks one with CONNECT to open a tunnel (RFC 9110 §9.3.6); and what that means for what was kept.
/// </summary>
public sealed record HttpProxyUse(string Kind, string Statement);

/// <summary>
/// Requests WinINet makes through a proxy, as FX-HTTP-010 measured them: one over HTTP is kept as WinINet sent it to the
/// proxy, its target the whole address; over TLS, WinINet first asks the proxy to CONNECT a tunnel, which its capture keeps
/// as an exchange of its own, and keeps the requests it then sends through the tunnel as over TLS directly, each an
/// exchange of its own, which no record ties to the CONNECT.
/// </summary>
public static class HttpProxies
{
    /// <summary>What a listing says once, where any exchange it lists went to a proxy.</summary>
    public const string Statement = "A request naming its target's whole address went to a proxy, as only a request to one "
        + "does (RFC 9112 §3.2.2), and WinINet keeps it as it sent it there; a CONNECT asked a proxy to open a tunnel, and "
        + "WinINet keeps it as an exchange of its own, and the requests it then sends through the tunnel over TLS as over TLS "
        + "directly, each an exchange of its own that no record ties to the CONNECT (FX-HTTP-010).";

    /// <summary>
    /// How <paramref name="heads"/>' request went to a proxy: a CONNECT, or a request whose target is an absolute HTTP or
    /// HTTPS address; null where its request line was not decoded, or is neither.
    /// </summary>
    public static HttpProxyUse? Of(HttpExchangeHeads heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.RequestLine is not { } line)
        {
            return null;
        }

        if (line.Method == "CONNECT")
        {
            return new("tunnel", $"It asked a proxy to open a tunnel to {line.Target}. WinINet keeps a CONNECT as an exchange "
                + "of its own, and the requests it then sends through the tunnel over TLS as over TLS directly, each an exchange "
                + "of its own that no record ties to this one (FX-HTTP-010).");
        }

        return Uri.TryCreate(line.Target, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https"
            ? new("request", $"It asked a proxy for {line.Target}: a client names a request's whole address only to a proxy "
                + "(RFC 9112 §3.2.2), and WinINet keeps the request as it sent it there (FX-HTTP-010).")
            : null;
    }
}
