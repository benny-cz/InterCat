using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>
/// A response that challenges its client for credentials (RFC 9110 §11.6, §11.7): <paramref name="Status"/> 401 naming
/// what it takes in WWW-Authenticate, or 407 in Proxy-Authenticate - <paramref name="Field"/> - its challenges as its head
/// gave them, <paramref name="Answer"/> the field a request answering it carries, and the target and host of the request
/// it challenged, each null where its request line was not decoded or named no host.
/// </summary>
public sealed record HttpChallenge(string Status, string Field, string Challenges, string Answer, string? Target, string? Host);

/// <summary>
/// The credentials a request carries (RFC 9110 §11.6.2, §11.7.2): the <paramref name="Field"/> that holds them and their
/// <paramref name="Scheme"/>, its value's first token - never the credentials themselves - and what that means for anyone
/// reading the heads.
/// </summary>
public sealed record HttpCredentials(string Field, string Scheme, string Statement);

/// <summary>
/// Challenges for credentials and the credentials that answer them, as WinINet meets them (`contracts/http-exchanges-v1.md`
/// §5i, FX-HTTP-011): it answers a challenge by itself where its client gave it credentials, and keeps the request it then
/// sends with them as an exchange of its own; once answered, it sends them with every later request to that server
/// unasked. No record ties a challenge to its answer, so the exchange that answered one is found by what it asked for: the
/// first listed after it, begun once its response ended, that asked for its target of its host carrying credentials.
/// </summary>
public static class HttpChallenges
{
    /// <summary>What WinINet does with a challenge, as FX-HTTP-011 measured it, said where a listing holds one.</summary>
    public const string Statement = "WinINet answers a challenge for credentials by itself where its client gave it some, and "
        + "keeps the request it then sends with them as an exchange of its own; once one is answered it sends them with every "
        + "later request to that server unasked, so most requests carry them without a challenge (FX-HTTP-011). A challenge "
        + "is said to be answered by the first exchange listed after it, begun once its response ended, that asked for its "
        + "target of its host carrying credentials.";

    /// <summary>What a listing says once where any request it lists carries credentials.</summary>
    public const string CredentialsStatement = "A request carrying credentials keeps them in its head as WinINet sent them, and "
        + "these decoded heads hold them: Basic credentials are a user name and password, encoded, not encrypted, so the heads "
        + "are to be shared only as those credentials would be (FX-HTTP-011).";

    /// <summary>
    /// The challenge <paramref name="heads"/>' response makes: a 401 whose head, decoded to its end, names WWW-Authenticate,
    /// or a 407 naming Proxy-Authenticate; null otherwise.
    /// </summary>
    public static HttpChallenge? Of(HttpExchangeHeads heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.StatusLine is not { Status: "401" or "407" } line || heads.Response is not { ReachedEnd: true } response)
        {
            return null;
        }

        (string field, string answer) = line.Status == "401" ? ("WWW-Authenticate", "Authorization") : ("Proxy-Authenticate", "Proxy-Authorization");
        string[] challenges = [.. response.Fields.Where(each => string.Equals(each.Name, field, StringComparison.OrdinalIgnoreCase))
            .Select(each => each.Value).Where(value => value.Length > 0)];
        if (challenges.Length == 0)
        {
            return null;
        }

        (string Target, string? Host)? asked = HttpRedirects.Asked(heads);
        return new(line.Status, field, string.Join(", ", challenges), answer, asked?.Target, asked?.Host);
    }

    /// <summary>
    /// The credentials <paramref name="heads"/>' request carries: its first Authorization or Proxy-Authorization field
    /// holding a value, by its scheme; null where its head was not decoded or holds neither.
    /// </summary>
    public static HttpCredentials? CredentialsOf(HttpExchangeHeads heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.Request?.Fields.FirstOrDefault(field => (string.Equals(field.Name, "Authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.Name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) && field.Value.Length > 0) is not { } carried)
        {
            return null;
        }

        int space = carried.Value.IndexOf(' ', StringComparison.Ordinal);
        string scheme = space < 0 ? carried.Value : carried.Value[..space];
        string readable = scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            ? ": a user name and password, encoded, not encrypted, which anyone reading these heads can read"
            : string.Empty;
        return new(carried.Name, scheme, $"It carries credentials in its {carried.Name} field, in the {scheme} scheme, kept as "
            + $"WinINet sent them{readable} (FX-HTTP-011).");
    }

    /// <summary>
    /// For each of <paramref name="exchanges"/> - a process's listed exchanges in reading order, beside their heads where
    /// they were decoded - the position of the listed exchange that answered its challenge: the first after it, begun once
    /// its response ended, that asked for its target of its host carrying the field that answers it; -1 where it makes no
    /// challenge, or none listed answered it. Where a host is not known of either, the target alone is matched.
    /// </summary>
    public static int[] AnsweredBy(IReadOnlyList<HttpExchange> exchanges, IReadOnlyList<HttpExchangeHeads?> heads)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(heads);
        if (heads.Count != exchanges.Count)
        {
            throw new ArgumentException("Each listed exchange needs its heads, or null where they were not decoded.", nameof(heads));
        }

        int[] answers = new int[exchanges.Count];
        Array.Fill(answers, -1);
        (string Target, string? Host)?[] asked = [.. heads.Select(head => head is null ? null : HttpRedirects.Asked(head))];
        for (int position = 0; position < exchanges.Count; position++)
        {
            if (heads[position]?.Challenge is not { Target: { } target } challenge)
            {
                continue;
            }

            long? ended = exchanges[position].ResponseEndNanoseconds;
            for (int later = position + 1; later < exchanges.Count; later++)
            {
                if (asked[later] is not { } request || request.Target != target) continue;
                if (challenge.Host is { } wanted && request.Host is { } named && !string.Equals(wanted, named, StringComparison.OrdinalIgnoreCase)) continue;
                if (ended is { } end && exchanges[later].FirstNanoseconds is { } begun && begun < end) continue;
                if (heads[later]?.Credentials is not { } carried || !string.Equals(carried.Field, challenge.Answer, StringComparison.OrdinalIgnoreCase)) continue;
                answers[position] = later;
                break;
            }
        }

        return answers;
    }
}
