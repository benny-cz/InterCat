using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How a paired channel's two ends were paired, in one set of words for the inspector and <c>icat channels</c> (R18,
/// §6.8: a channel is one action from the rule, version and coverage behind it).
/// </summary>
public static class ChannelPairingText
{
    /// <summary>
    /// The rule that paired its ends and how strongly the pairing holds, whether the capture saw the connection open and
    /// close, and the capture's coverage over the session. A channel no rule paired, as the tour's, has none to explain.
    /// </summary>
    public static string Explain(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Rule is not { } rule)
        {
            throw new ArgumentException("A channel no rule paired has no pairing to explain.", nameof(channel));
        }

        string strength = channel.Strength == RelationStrength.Candidate
            ? "as a candidate, since one end's records bind to their process only as candidates"
            : "correlated";
        string lifetime = ConnectionSummary.LifetimeWords(channel.OpenWitnessed, channel.CloseWitnessed);
        return $"Each end's records bind to one process and name the other end: paired by {ProcessBindingText.Rule(rule)}, {strength}. "
            + $"{char.ToUpperInvariant(lifetime[0])}{lifetime[1..]}. Coverage over the session: {CoverageStateText.Value(channel.Coverage)}.";
    }
}
