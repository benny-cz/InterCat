using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// `--evidence-policy` as a person types it: a policy's name as written or in lower-case kebab form, so `direct-only` and
/// `DirectOnly` are the same request, as the window and `icat patterns` cite it (<see cref="CommandArgument.Typed"/>). A
/// number is refused: codes are what the durable format stores, not what a person types.
/// </summary>
internal static class EvidencePolicyOption
{
    /// <summary>Reads <paramref name="text"/>, <see cref="EvidencePolicy.IncludeCorrelated"/> where it is null; false with the refusal otherwise.</summary>
    public static bool TryParse(string? text, out EvidencePolicy policy, out string? refusal)
    {
        policy = EvidencePolicy.IncludeCorrelated;
        refusal = null;
        if (text is null) return true;
        string compact = text.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length > 0 && !char.IsAsciiDigit(compact[0]) && Enum.TryParse(compact, ignoreCase: true, out policy)
            && Enum.IsDefined(policy))
        {
            return true;
        }

        policy = EvidencePolicy.IncludeCorrelated;
        refusal = CommandLine.Expects("--evidence-policy", text, CommandLine.Kebabs<EvidencePolicy>());
        return false;
    }

    /// <summary>
    /// A policy a document names as the durable format stores it, <c>IncludeCorrelated</c>, as a person types it,
    /// <c>include-correlated</c>, which is how every sentence of icat names one; a name no policy has as it is.
    /// </summary>
    public static string Typed(string named) =>
        Enum.TryParse(named, out EvidencePolicy policy) && Enum.IsDefined(policy) ? CommandArgument.Typed(policy) : named;
}
