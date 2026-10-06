using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The capture's coverage over a scope, judged and said once for every layer that states it beside its counts (R21): the
/// inspector beneath its time scope, and <c>icat evidence</c> beside the records it lists. A scope's coverage is each
/// mechanism's own, never one rolled-up state (metrics-v1 §7).
/// </summary>
public static class CoverageText
{
    /// <summary>
    /// Each mechanism's coverage over a presentation interval of a generation's readings, or over every epoch without
    /// one (`coverage-v2` §4). An interval no reading of the clock falls in, such as one narrower than a tick, is unknown
    /// for every mechanism: nothing could be read there, and the whole session's coverage is not that interval's.
    /// </summary>
    public static IReadOnlyList<MechanismCoverage> Over(CoverageLedgerV1? ledger, SourceClockDescriptor? clock, TimeRange? interval)
    {
        if (interval is not { } range)
        {
            return SessionCoverage.ByMechanism(ledger);
        }

        return clock is { } readings && RankingScope.NativeInterval(readings, range) is { } native
            ? SessionCoverage.ByMechanism(ledger, native)
            : Unplaced;
    }

    /// <summary>
    /// What the capture covered over a scope, in words: each state with the mechanisms in it, the fact behind any state
    /// short of covered, and that no other mechanism was collected, so no count of one was possible. When nothing is
    /// known of any mechanism for one reason - no ledger, or a scope outside every reading the capture delivered - that
    /// reason is said once, with what it means for a count of none. Empty when nothing was judged.
    /// </summary>
    public static string Describe(IReadOnlyList<MechanismCoverage> mechanisms)
    {
        ArgumentNullException.ThrowIfNull(mechanisms);
        if (mechanisms.Count == 0)
        {
            return string.Empty;
        }

        if (mechanisms.All(entry => entry.State == CoverageState.UnknownCoverage
            && string.Equals(entry.Reason, mechanisms[0].Reason, StringComparison.Ordinal)))
        {
            return $"Coverage unknown: {mechanisms[0].Reason}, so a count of none here is not proof of inactivity";
        }

        MechanismCoverage[] collected = [.. mechanisms.Where(entry => entry.State != CoverageState.NotCollected)];
        if (collected.Length == 0)
        {
            return "Coverage: no mechanism was collected here, so a count of none here is not proof of inactivity";
        }

        // Mechanisms that share a state, and the fact behind it, are named together; a covered one needs no fact.
        List<string> parts =
        [
            .. collected
                .GroupBy(entry => (entry.State, Reason: entry.State == CoverageState.Covered ? string.Empty : entry.Reason))
                .OrderBy(group => group.Key.State)
                .ThenBy(group => group.First().Mechanism)
                .Select(group =>
                {
                    string names = List([.. group.Select(entry => EvidenceRowText.MechanismInSentence(entry.Mechanism))]);
                    return group.Key.State switch
                    {
                        CoverageState.Covered => $"covered for {names}",
                        CoverageState.ReducedFidelity => $"reduced fidelity for {names}: {group.Key.Reason}",
                        CoverageState.PartialGap => $"a partial gap, not extrapolated, for {names}: {group.Key.Reason}",
                        _ => $"unknown for {names}: {group.Key.Reason}",
                    };
                }),
        ];
        if (collected.Length < mechanisms.Count)
        {
            parts.Add("no other mechanism collected");
        }

        return "Coverage: " + string.Join(" · ", parts);
    }

    /// <summary>
    /// Whether a scope's coverage falls short of covered: a collected mechanism was judged anything less, or nothing was
    /// collected. A scope that did not collect some mechanism is not short for it: its counts are as complete as the
    /// capture's sources allowed, and its words say what it did not collect.
    /// </summary>
    public static bool IsShort(IReadOnlyList<MechanismCoverage> mechanisms)
    {
        ArgumentNullException.ThrowIfNull(mechanisms);
        MechanismCoverage[] collected = [.. mechanisms.Where(entry => entry.State != CoverageState.NotCollected)];
        return mechanisms.Count > 0 && (collected.Length == 0 || collected.Any(entry => entry.State != CoverageState.Covered));
    }

    private static readonly IReadOnlyList<MechanismCoverage> Unplaced = Array.AsReadOnly(
    [
        .. Enum.GetValues<Mechanism>().Select(mechanism => new MechanismCoverage(
            mechanism, CoverageState.UnknownCoverage, "no reading of the capture's clock falls in this interval")),
    ]);

    /// <summary>Several names as a sentence lists them: "TCP", "TCP and UDP", "process lifecycle, TCP and UDP".</summary>
    private static string List(IReadOnlyList<string> names) => names.Count < 2
        ? string.Concat(names)
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
