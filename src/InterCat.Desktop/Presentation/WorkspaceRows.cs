using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// One legend entry. The glyph and the label are the redundant channels that carry the mechanism without
/// relying on hue, so the legend stays readable in greyscale and under colour-vision differences (R14).
/// </summary>
public sealed record LegendEntry(string Label, string Glyph, string FillHex, string InkHex)
{
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
    string Evidence)
{
    public string AccessibleName => string.Create(
        CultureInfo.CurrentCulture,
        $"{Source} to {Target} over {Mechanism}, {Observations} observations, {KnownBytes}, evidence {Evidence}");
}

/// <summary>A timeline cell as a table row, carrying the same counts and the same coverage state.</summary>
public sealed record IntervalRow(
    TimeRange Interval,
    string Window,
    string Observations,
    string KnownBytes,
    string Mechanism,
    string Glyph,
    string Coverage)
{
    public string AccessibleName => string.Create(
        CultureInfo.CurrentCulture,
        $"{Window}, {Observations} observations, {KnownBytes}, {Mechanism}, coverage {Coverage}");
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
                DescribeStrength(edge.Strength)));
        }

        return rows;
    }

    public static IReadOnlyList<IntervalRow> Intervals(WorkspaceSnapshot snapshot, ThemeMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = new List<IntervalRow>(snapshot.Timeline.Count);
        foreach (TimelineBucket bucket in snapshot.Timeline)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(bucket.DominantMechanism));
            decimal from = bucket.Interval.StartTicks / (decimal)WorkspaceTime.TicksPerSecond;
            decimal to = bucket.Interval.EndTicks / (decimal)WorkspaceTime.TicksPerSecond;
            rows.Add(new(
                bucket.Interval,
                string.Create(CultureInfo.CurrentCulture, $"{from:N0} s to {to:N0} s"),
                bucket.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                DescribeBytes(bucket.KnownBytes),
                tokens.Label,
                tokens.Glyph,
                DescribeCoverage(bucket.Coverage)));
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
