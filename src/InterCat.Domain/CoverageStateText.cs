namespace InterCat.Domain;

/// <summary>
/// A coverage state as InterCat names it to a person, one mapping for every layer (R5): the window's rows and hover
/// cards and the command line's tables say "partial gap, not extrapolated" where the enumeration says <c>PartialGap</c>.
/// A sentence that states one mechanism's coverage says it in its own form (<c>SessionCoverage.Sentence</c>).
/// </summary>
public static class CoverageStateText
{
    /// <summary>
    /// A state where something names it as coverage - "Coverage: unknown", a column headed Coverage: "covered",
    /// "reduced fidelity", "partial gap, not extrapolated", "not collected", "unknown".
    /// </summary>
    public static string Value(CoverageState state) => state switch
    {
        CoverageState.Covered => "covered",
        CoverageState.ReducedFidelity => "reduced fidelity",
        CoverageState.PartialGap => "partial gap, not extrapolated",
        CoverageState.NotCollected => "not collected",
        _ => "unknown",
    };

    /// <summary>A state as a label standing alone, as a row's detail shows it: its value, with unknown said as "unknown coverage".</summary>
    public static string Label(CoverageState state) =>
        state is CoverageState.Covered or CoverageState.ReducedFidelity or CoverageState.PartialGap or CoverageState.NotCollected
            ? Value(state)
            : "unknown coverage";
}
