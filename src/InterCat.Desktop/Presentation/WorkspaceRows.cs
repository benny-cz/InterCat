using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// One legend entry. The glyph and the label are the redundant channels that carry the mechanism without
/// relying on hue, so the legend stays readable in greyscale and under colour-vision differences (R14).
/// </summary>
public sealed record LegendEntry(string Label, string Glyph, string FillHex, string InkHex) : IAccessibleRow
{
    /// <summary>The mechanism family's name; its glyph and hue repeat what the word says.</summary>
    public string AccessibleName => Label;

    public static LegendEntry For(Mechanism mechanism, ThemeMode mode)
    {
        FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(mechanism));
        return new(tokens.Label, tokens.Glyph, tokens.Fill.ToHex(), tokens.Ink.ToHex());
    }
}

/// <summary>
/// A relationship as a table row. It yields the same result set as the graph edge it mirrors, so the
/// canvas is never the only way to reach an edge (R15).
/// </summary>
public sealed record RelationshipRow(
    ProcessInstanceId SourceId,
    string Source,
    string Target,
    string Mechanism,
    string Glyph,
    string Observations,
    string KnownBytes,
    string Evidence) : IAccessibleRow
{
    /// <summary>The relationship's observation count, which <see cref="Observations"/> shows formatted.</summary>
    public long ObservationCount { get; init; }

    public string AccessibleName =>
        $"{Source} to {Target} over {Mechanism}, {Spoken.Count(ObservationCount, "observation")}, {KnownBytes}, "
        + $"evidence {Evidence}";
}

/// <summary>A timeline cell as a table row, carrying the same counts and the same coverage state.</summary>
public sealed record IntervalRow(
    TimeRange Interval,
    string Window,
    string Observations,
    string KnownBytes,
    string Mechanism,
    string Glyph,
    string Coverage) : IAccessibleRow
{
    /// <summary>The window's observation count and its focus count, which <see cref="Observations"/> shows formatted.</summary>
    public long ObservationCount { get; init; }

    public long? FocusCount { get; init; }

    public string AccessibleName =>
        $"{Window}, {Spoken.Count(ObservationCount, "observation")}"
        + (FocusCount is { } focused ? string.Create(CultureInfo.CurrentCulture, $", {focused:N0} in focus") : string.Empty)
        + $", {KnownBytes}, {Mechanism}, {Spoken.Coverage(Coverage)}";
}

/// <summary>Builds the table equivalents from one snapshot, using the same values the canvas draws.</summary>
public static class WorkspaceRowBuilder
{
    public static IReadOnlyList<LegendEntry> Legend(WorkspaceSnapshot snapshot, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var seen = new List<Mechanism>();
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            if (!seen.Contains(edge.Mechanism))
            {
                seen.Add(edge.Mechanism);
            }
        }

        foreach (TimelineBucket bucket in snapshot.Timeline)
        {
            if (!seen.Contains(bucket.DominantMechanism))
            {
                seen.Add(bucket.DominantMechanism);
            }
        }

        var entries = new List<LegendEntry>(seen.Count);
        foreach (Mechanism mechanism in seen)
        {
            LegendEntry entry = LegendEntry.For(mechanism, mode);
            if (!entries.Contains(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public static IReadOnlyList<RelationshipRow> Relationships(WorkspaceSnapshot snapshot, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = new List<RelationshipRow>(snapshot.Edges.Count);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(edge.Mechanism));
            rows.Add(new(
                edge.SourceId,
                NameOf(snapshot, edge.SourceId),
                NameOf(snapshot, edge.TargetId),
                tokens.Label,
                tokens.Glyph,
                edge.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                DescribeBytes(edge.KnownBytes),
                DescribeStrength(edge.Strength))
            {
                ObservationCount = edge.ObservationCount,
            });
        }

        return rows;
    }

    public static IReadOnlyList<IntervalRow> Intervals(WorkspaceSnapshot snapshot, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Intervals(snapshot.Timeline, mode);
    }

    /// <summary>
    /// The interval table for the buckets the timeline draws. Each window is stated in the unit its span needs, so a
    /// 20-ms bucket never reads "5 s to 5 s" (§6.2 escalates units the same way). A focused rung's count for the same
    /// window stands beside the window's own, as its colour stands inside the window's grey bar (§3.2).
    /// </summary>
    public static IReadOnlyList<IntervalRow> Intervals(
        IReadOnlyList<TimelineBucket> buckets, ThemeMode mode, IReadOnlyList<TimelineBucket>? focus = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        Dictionary<TimeRange, int>? inFocus = focus?.ToDictionary(bucket => bucket.Interval, bucket => bucket.ObservationCount);
        var rows = new List<IntervalRow>(buckets.Count);
        foreach (TimelineBucket bucket in buckets)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(bucket.DominantMechanism));
            string observations = bucket.ObservationCount.ToString("N0", CultureInfo.CurrentCulture);
            int? focusCount = null;
            if (inFocus is not null && inFocus.TryGetValue(bucket.Interval, out int focused))
            {
                observations += string.Create(CultureInfo.CurrentCulture, $" · {focused:N0} in focus");
                focusCount = focused;
            }

            rows.Add(new(
                bucket.Interval,
                WorkspaceTime.FormatRange(bucket.Interval, CultureInfo.CurrentCulture),
                observations,
                DescribeBytes(bucket.KnownBytes),
                tokens.Label,
                tokens.Glyph,
                DescribeCoverage(bucket.Coverage))
            {
                ObservationCount = bucket.ObservationCount,
                FocusCount = focusCount,
            });
        }

        return rows;
    }

    /// <summary>
    /// Renders a byte value. An unknown value is stated as unknown and never shown as zero, and a measured
    /// zero is shown as zero (R3, P1).
    /// </summary>
    public static string DescribeBytes(long? value) => value is null
        ? "bytes unknown"
        : string.Create(CultureInfo.CurrentCulture, $"{value.Value / 1_000_000m:N2} MB known");

    private static string DescribeStrength(RelationStrength strength) => strength switch
    {
        RelationStrength.Direct => "direct",
        RelationStrength.Correlated => "correlated",
        RelationStrength.Candidate => "candidate",
        RelationStrength.Unresolved => "unresolved",
        _ => "conflicting",
    };

    private static string DescribeCoverage(CoverageState coverage) => coverage switch
    {
        CoverageState.Covered => "covered",
        CoverageState.ReducedFidelity => "reduced fidelity",
        CoverageState.PartialGap => "partial gap, not extrapolated",
        CoverageState.NotCollected => "not collected",
        _ => "unknown coverage",
    };

    private static string NameOf(WorkspaceSnapshot snapshot, ProcessInstanceId id)
    {
        foreach (ProcessNode node in snapshot.Processes)
        {
            if (node.Id == id)
            {
                return node.Name;
            }
        }

        return "unknown process";
    }
}
