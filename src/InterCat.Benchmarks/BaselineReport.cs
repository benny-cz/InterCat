using InterCat.Domain;

namespace InterCat.Benchmarks;

/// <summary>
/// One supported build and whether its fixture corpus has been run. §13.4 asks for the corpus on every
/// supported build; this is the backlog that states which of them still owe it, so "supported" cannot
/// quietly mean "supported in principle" (§1.3, §13.4).
/// </summary>
public sealed record BuildValidationEntry(
    string Name,
    int BuildNumber,
    string Architecture,
    BuildSupportTier Tier,
    bool IsPrerelease,
    bool CorpusMeasured,
    string Evidence);

/// <summary>
/// The IC-010 baseline: the machine a result was taken on, the §12 budget table with what measured each
/// entry, and the per-build validation backlog. It is a reproducible artifact, not a claim: a budget
/// nothing measured says so, and a build nobody ran says so.
/// </summary>
public sealed record BaselineReport
{
    public required string Schema { get; init; }
    public required DateTimeOffset ProducedUtc { get; init; }
    public required MachineDescriptor Machine { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required IReadOnlyList<BudgetResult> Budgets { get; init; }
    public required IReadOnlyList<BuildValidationEntry> BuildBacklog { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public int BudgetsMet => Budgets.Count(result => result.Outcome == BudgetOutcome.Met);

    public int BudgetsMissed => Budgets.Count(result => result.Outcome == BudgetOutcome.Missed);

    public int BudgetsUnmeasured => Budgets.Count(result => result.Outcome == BudgetOutcome.Unmeasured);

    /// <summary>True only when every §12 budget is measured and met. Nothing here is met by default.</summary>
    public bool EveryBudgetMet => BudgetsMissed == 0 && BudgetsUnmeasured == 0;
}

/// <summary>Builds the per-build validation backlog from the §1.3 matrix and what has been measured.</summary>
public static class BuildValidationBacklog
{
    /// <summary>
    /// Marks the builds whose fixture corpus has been run. Everything else in the matrix is listed as
    /// owing it, which is what makes the backlog explicit rather than implied (IC-010).
    /// </summary>
    public static IReadOnlyList<BuildValidationEntry> Build(
        IReadOnlyDictionary<int, string> measuredBuildEvidence)
    {
        ArgumentNullException.ThrowIfNull(measuredBuildEvidence);

        var entries = new List<BuildValidationEntry>(SupportedBuilds.All.Count);
        foreach (SupportedBuild build in SupportedBuilds.All)
        {
            bool measured = measuredBuildEvidence.TryGetValue(build.BuildNumber, out string? evidence);
            entries.Add(new(
                build.Name,
                build.BuildNumber,
                build.Architecture,
                build.Tier,
                build.IsPrerelease,
                measured,
                measured
                    ? evidence!
                    : "The fixture corpus of section 13.4 has not been run on this build. Every tier above "
                        + "rests on the builds that were."));
        }

        return entries;
    }
}
