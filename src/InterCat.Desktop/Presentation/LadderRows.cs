using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// One row of the current rung's ranked table. It carries the glyph and the word beside the hue, so the
/// mechanism is readable without colour, and it states what the row descends into (R14, section 3.2).
/// </summary>
public sealed record RungRow(
    string Key,
    string Label,
    string Detail,
    string Observations,
    string KnownBytes,
    string Mechanism,
    string Glyph,
    string Coverage,
    string DescendsTo,
    LadderRow Source) : IAccessibleRow
{
    /// <summary>The sentence a screen reader hears when the ranked-row wording does not fit, as for a source record.</summary>
    public string? SpokenName { get; init; }

    /// <summary>
    /// The row's second line: what it holds, then its coverage. Coverage shares the line under the name rather than the
    /// count's column, where a phrase such as "partial gap, not extrapolated" left the name a few letters in the rail.
    /// </summary>
    public string DetailLine => Coverage.Length == 0 ? Detail
        : Detail.Length == 0 ? Coverage
        : $"{Detail} · {Coverage}";

    public string AccessibleName => SpokenName
        ?? $"{Label}, {Detail}, {Spoken.Count(Source.ObservationCount, "observation")}, {KnownBytes}, "
            + $"{Mechanism}, {Spoken.Coverage(Coverage)}. Press Enter to open the {DescendsTo} level.";
}

/// <summary>One crumb of the breadcrumb. Selecting it returns to that rung, which is why it has a depth.</summary>
public sealed record CrumbRow(int Depth, string Label, bool IsCurrent) : IAccessibleRow
{
    public string AccessibleName => IsCurrent
        ? $"{Label}, the current level"
        : $"{Label}, press Enter to return to this level";
}

/// <summary>A filter a descent implied, shown so it can be seen and removed (section 3.2).</summary>
public sealed record FilterRow(string Field, string Label, string Reason) : IAccessibleRow
{
    public string AccessibleName => $"Filter {Field} is {Label}. {Reason} Press Enter to remove it.";
}

/// <summary>Builds the ladder's presentation rows from a projection, using the projection's own values.</summary>
public static class LadderRowBuilder
{
    public static IReadOnlyList<RungRow> Rows(LadderView view, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(view);

        var rows = new List<RungRow>(view.Rows.Count);
        foreach (LadderRow row in view.Rows)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(row.Mechanism));
            rows.Add(new(
                row.Key,
                row.Label,
                row.Detail,
                row.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                WorkspaceRowBuilder.DescribeBytes(row.KnownBytes),
                tokens.Label,
                tokens.Glyph,
                DescribeCoverage(row.Coverage),
                NavigationState.Name(row.DescendsTo),
                row));
        }

        return rows;
    }

    public static IReadOnlyList<CrumbRow> Crumbs(DetailLadder ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        var crumbs = new List<CrumbRow>(ladder.Breadcrumb.Count);
        for (int depth = 0; depth < ladder.Breadcrumb.Count; depth++)
        {
            crumbs.Add(new(depth, ladder.Breadcrumb[depth].Crumb, depth == ladder.Depth));
        }

        return crumbs;
    }

    public static IReadOnlyList<FilterRow> Filters(NavigationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. state.Filters.Select(filter => new FilterRow(filter.Field, filter.Value, filter.Reason))];
    }

    /// <summary>
    /// The rung's total, or the reason it has none. Rows that overlap are never added, so a rung whose
    /// rows share their observations says so instead of printing a number that counts twice (section 5.1).
    /// </summary>
    public static string DescribeTotal(LadderView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.ObservationCount is not { } total)
        {
            return "no single total: " + (view.TotalUnavailableReason ?? "these rows overlap");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{total:N0} observations · {WorkspaceRowBuilder.DescribeBytes(view.KnownBytes)}");
    }

    /// <summary>
    /// The same total in one line, for the narrow rail. The full reason stays in the inspector, so the
    /// explanation is never lost, only stated once at length.
    /// </summary>
    public static string DescribeTotalShort(LadderView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.ObservationCount is { } total
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{total:N0} observations · {WorkspaceRowBuilder.DescribeBytes(view.KnownBytes)}")
            : "no single total: these rows overlap";
    }

    private static string DescribeCoverage(CoverageState coverage) => coverage switch
    {
        CoverageState.Covered => "covered",
        CoverageState.ReducedFidelity => "reduced fidelity",
        CoverageState.PartialGap => "partial gap, not extrapolated",
        CoverageState.NotCollected => "not collected",
        _ => "unknown coverage",
    };
}
