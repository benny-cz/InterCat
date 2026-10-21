using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The <c>icat evidence</c> query an evidence export's records answer (R18, I16): the identity its pages carry, the one
/// <c>icat evidence --json</c> prints and its cursors are bound to, and the command that lists the same records.
/// </summary>
/// <param name="Identity">The page's query identity: SHA-256 of its scope, rules and derivation, in lowercase hex.</param>
/// <param name="Command">The command that lists the records, the session's directory left for a person to name.</param>
public sealed record EvidenceQueryName(string Identity, string Command);

/// <summary>
/// Names the query an evidence page answers. A page's scope - one paired channel or other host, a set of owner processes,
/// an RPC or HTTP key, an interval and a lane's narrowing - and the evidence policy it bound records under are each an
/// option of <c>icat evidence</c>, so every scope the window's evidence rung reads is one command.
/// </summary>
public static class EvidenceQuery
{
    /// <summary>The identity's form, as an export names it beside the identity.</summary>
    public const string Form = "evidence-page-v2";

    /// <summary>The query <paramref name="page"/> answers, its records bound under <paramref name="policy"/>.</summary>
    public static EvidenceQueryName Name(SessionEvidencePage page, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new(page.QueryIdentity, Command(page, policy));
    }

    /// <summary>
    /// The command that lists <paramref name="page"/>'s records from its first: its channel, host, RPC or HTTP key, owner
    /// processes, interval in 100-nanosecond session ticks and narrowing, and the evidence policy where it is not the
    /// default, as <c>icat evidence</c> reads each.
    /// </summary>
    public static string Command(SessionEvidencePage page, EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(page);
        var words = new List<string> { "icat evidence <directory>" };
        if ((page.OperationKey ?? page.ChannelKey) is { } key)
        {
            words.Add("--channel " + CommandArgument.Of(key));
        }

        words.AddRange(page.OwnerProcesses.Select(owner => "--owner-process " + owner));
        if (page.Interval is { } interval)
        {
            words.Add(string.Create(CultureInfo.InvariantCulture, $"--interval {interval.StartTicks}:{interval.EndTicks}"));
        }

        if (page.Mechanism is { } mechanism) words.Add("--mechanism " + Kebab(mechanism.ToString()));
        if (page.Direction is { } direction) words.Add("--direction " + Kebab(direction.ToString()));
        if (page.End is { } end) words.Add(string.Create(CultureInfo.InvariantCulture, $"--end {end}"));
        if (policy != EvidencePolicy.IncludeCorrelated) words.Add("--evidence-policy " + Kebab(policy.ToString()));
        return string.Join(' ', words);
    }

    /// <summary>An enumeration's name as a person types it: "DirectOnly" as "direct-only".</summary>
    private static string Kebab(string name) => string.Concat(name.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
