using System.Globalization;
using Avalonia.Media;
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

    /// <summary>The chip glyph in the family's fill, the hue its marks are drawn in: what makes the legend a key (§6.6).</summary>
    public IBrush FillBrush => SolidColorBrush.Parse(FillHex);

    /// <summary>The family's name in its ink variant, verified for text on the theme's grounds (§6.6).</summary>
    public IBrush InkBrush => SolidColorBrush.Parse(InkHex);

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

    /// <summary>
    /// Whether the row states bytes. A real session's timeline sums none per interval, so its rows leave the column out
    /// rather than calling every interval's bytes unknown.
    /// </summary>
    public bool ShowsBytes { get; init; } = true;

    public string AccessibleName =>
        $"{Window}, {Spoken.Count(ObservationCount, "observation")}"
        + (FocusCount is { } focused ? string.Create(CultureInfo.CurrentCulture, $", {focused:N0} in focus") : string.Empty)
        + (ShowsBytes ? $", {KnownBytes}" : string.Empty) + $", {Mechanism}, {Spoken.Coverage(Coverage)}";
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

        // An empty bucket draws no mark, so the placeholder mechanism it carries keys nothing (§6.6: a key shows only
        // marks a pane draws). Revision 167 found every session keyed "Unknown" for its empty columns.
        foreach (TimelineBucket bucket in snapshot.Timeline)
        {
            if (bucket.ObservationCount > 0 && !seen.Contains(bucket.DominantMechanism))
            {
                seen.Add(bucket.DominantMechanism);
            }
        }

        // A mechanism lane draws its records in its family's colour whether or not they dominate any column.
        foreach (MechanismTimelineLane lane in snapshot.MechanismLanes)
        {
            if (!seen.Contains(lane.Mechanism) && lane.Buckets.Any(bucket => bucket.ObservationCount > 0))
            {
                seen.Add(lane.Mechanism);
            }
        }

        // In the lanes' order, by mechanism code, so the key reads in the order the timeline draws the lanes rather than
        // in whichever order the first edge and the first bucket happened to show them.
        var entries = new List<LegendEntry>(seen.Count);
        foreach (Mechanism mechanism in seen.Order())
        {
            LegendEntry entry = LegendEntry.For(mechanism, mode);
            if (!entries.Contains(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// The relationship table's rows, each end named with its PID, since one executable's instances otherwise read as the
    /// same row. Where <paramref name="bytesOf"/> is given, as for a real session, whose overview sums no bytes, it says
    /// each relationship's bytes: what was read as sent across it, or why that is not known, rather than calling them
    /// unknown.
    /// </summary>
    public static IReadOnlyList<RelationshipRow> Relationships(
        WorkspaceSnapshot snapshot, ThemeMode mode, Func<CommunicationEdge, string>? bytesOf = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<ProcessInstanceId, string> names = [];
        foreach (ProcessNode node in snapshot.Processes)
        {
            names.TryAdd(node.Id, node.NameWithPid);
        }

        var rows = new List<RelationshipRow>(snapshot.Edges.Count);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(edge.Mechanism));
            rows.Add(new(
                edge.SourceId,
                names.GetValueOrDefault(edge.SourceId, "unknown process"),
                names.GetValueOrDefault(edge.TargetId, "unknown process"),
                tokens.Label,
                tokens.Glyph,
                edge.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                bytesOf is null ? DescribeBytes(edge.KnownBytes) : bytesOf(edge),
                DescribeStrength(edge.Strength))
            {
                ObservationCount = edge.ObservationCount,
            });
        }

        return rows;
    }

    public static IReadOnlyList<IntervalRow> Intervals(WorkspaceSnapshot snapshot, ThemeMode mode, bool sumsBytes = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Intervals(snapshot.Timeline, mode, sumsBytes: sumsBytes);
    }

    /// <summary>
    /// The interval table for the buckets the timeline draws. Each window is stated in the unit its span needs, so a
    /// 20-ms bucket never reads "5 s to 5 s" (§6.2 escalates units the same way). A focused rung's count for the same
    /// window stands beside the window's own, as its colour stands inside the window's grey bar (§3.2). Where the
    /// timeline sums no bytes (<paramref name="sumsBytes"/> false), the rows state none.
    /// </summary>
    public static IReadOnlyList<IntervalRow> Intervals(
        IReadOnlyList<TimelineBucket> buckets, ThemeMode mode, IReadOnlyList<TimelineBucket>? focus = null,
        bool sumsBytes = true)
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
                ShowsBytes = sumsBytes,
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

    /// <summary>
    /// A measured byte total as the ranked table shows it, in the decimal units of <see cref="DescribeBytes"/> and three
    /// significant figures at most: "512 B", "8.4 KB", "37 MB", "1.3 GB". A value that rounds up to the next unit is
    /// written in it, so 999,999 bytes reads "1.0 MB" rather than "1,000 KB".
    /// </summary>
    public static string DescribeSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < 1_000)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B");
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB"];
        double scaled = bytes / 1_000d;
        int unit = 0;
        while (unit < units.Length - 1 && Math.Round(scaled, scaled < 10 ? 1 : 0) >= 1_000)
        {
            scaled /= 1_000;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture,
            $"{scaled.ToString(Math.Round(scaled, 1) < 10 ? "N1" : "N0", CultureInfo.CurrentCulture)} {units[unit]}");
    }

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
}
