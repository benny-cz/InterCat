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
    /// The row's value under a byte ranking as its right column shows it: "4.2 MB", "unmeasured" or "no sends"; null
    /// when the rung ranks by records, whose count the column shows.
    /// </summary>
    public string? RankedFigure { get; init; }

    /// <summary>The same value as a sentence says it, with the records behind it: "4.2 MB sent on 12 measured sends".</summary>
    public string? RankedSpoken { get; init; }

    /// <summary>The right column: the ranked value under a byte ranking, else the records.</summary>
    public string Figure => RankedFigure ?? Observations;

    /// <summary>
    /// The row's second line: what it holds, then its coverage. Coverage shares the line under the name rather than the
    /// count's column, where a phrase such as "partial gap, not extrapolated" left the name a few letters in the rail.
    /// Under a byte ranking the records the column no longer shows follow what the row holds.
    /// </summary>
    public string DetailLine
    {
        get
        {
            string holds = RankedFigure is null ? Detail
                : Detail.Length == 0 ? $"{Observations} records"
                : $"{Detail} · {Observations} records";
            return Coverage.Length == 0 ? holds
                : holds.Length == 0 ? Coverage
                : $"{holds} · {Coverage}";
        }
    }

    /// <summary>Whether the row's rung ranks by bytes, whose value its known-bytes phrase would contradict.</summary>
    private bool RanksByBytes => Source.Ranked is { Metric: RankingMetric.BytesSent or RankingMetric.BytesReceived };

    /// <summary>
    /// The row as a screen reader says it. Under a byte ranking the ranked bytes take the place of the paired channels'
    /// known bytes, which would be a second byte figure contradicting the first.
    /// </summary>
    public string AccessibleName => SpokenName
        ?? $"{Label}, {Detail}, {(RankedSpoken is { } ranked ? ranked + ", " : string.Empty)}"
            + $"{Spoken.Count(Source.ObservationCount, "observation")}, {(RanksByBytes || KnownBytes.Length == 0 ? string.Empty : KnownBytes + ", ")}"
            + $"{Mechanism}, {Spoken.Coverage(Coverage)}. Press Enter to open the {DescendsTo} level.";
}

/// <summary>One crumb of the breadcrumb. Selecting it returns to that rung, which is why it has a depth.</summary>
public sealed record CrumbRow(int Depth, string Label, bool IsCurrent) : IAccessibleRow
{
    /// <summary>
    /// What the crumb shows: its label, with a channel's endpoints abbreviated to keep their ports. The tooltip and the
    /// accessible name keep the whole label.
    /// </summary>
    public string Display { get; init; } = Label;

    public string AccessibleName => IsCurrent
        ? $"{Label}, the current level"
        : $"{Label}, press Enter to return to this level";
}

/// <summary>A filter a descent implied, shown so it can be seen and removed (section 3.2).</summary>
public sealed record FilterRow(string Field, string Label, string Reason) : IAccessibleRow
{
    public string AccessibleName => $"Filter {Field} is {Label}. {Reason} Press Enter to remove it.";

    /// <summary>
    /// The chip's words: what the filter narrows, then to what, as a crumb reads. A channel's filter and the evidence
    /// scope of the same channel otherwise showed the same endpoint pair twice, as if one filter were repeated.
    /// </summary>
    public string Chip => $"{Name}: {(Field == "channel" ? ChannelNames.Abbreviated(Label) : Label)}";

    /// <summary>The chip's tooltip: its whole text, which a long endpoint pair can cut short, and why it applies.</summary>
    public string Tip => $"{Name}: {Label}\n{Reason}";

    private string Name => Field switch
    {
        "group" => "Group",
        "process" => "Process",
        "channel" => "Channel",
        "operation" => "Operation",
        "scope" => "Records of",
        _ => Field,
    };
}

/// <summary>Builds the ladder's presentation rows from a projection, using the projection's own values.</summary>
public static class LadderRowBuilder
{
    /// <summary>
    /// The rung's presentation rows. Where <paramref name="bytesSummed"/> is false, as for a real session whose overview
    /// sums no bytes, a row with no byte total states none rather than calling its bytes unknown.
    /// </summary>
    public static IReadOnlyList<RungRow> Rows(LadderView view, ThemeMode mode, bool bytesSummed = true)
    {
        ArgumentNullException.ThrowIfNull(view);

        var rows = new List<RungRow>(view.Rows.Count);
        foreach (LadderRow row in view.Rows)
        {
            FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(row.Mechanism));
            rows.Add(new RungRow(
                row.Key,
                row.Label,
                row.Detail,
                row.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                row.KnownBytes is null && !bytesSummed ? string.Empty : WorkspaceRowBuilder.DescribeBytes(row.KnownBytes),
                tokens.Label,
                tokens.Glyph,
                DescribeCoverage(row.Coverage),
                NavigationState.Name(row.DescendsTo),
                row)
            {
                RankedFigure = row.Ranked is { } ranked ? RankedFigure(ranked) : null,
                RankedSpoken = row.Ranked is { } spoken ? RankedSpoken(spoken) : null,
            });
        }

        return rows;
    }

    /// <summary>
    /// A ranking's value in the right column. Under a byte ranking: the measured sum, or "unmeasured" when the row's
    /// records recorded no size, or "no sends" when it made none; neither of the last two is a zero (§5.2, R21). Under a
    /// call ranking: the calls completed, or "0 completed" when the row's only stops were paired with no start, or "no
    /// calls".
    /// </summary>
    public static string RankedFigure(RankedValue ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Metric is RankingMetric.RpcCallsMade or RankingMetric.RpcCallsServed)
        {
            return ranked.Value is { } calls
                ? string.Create(CultureInfo.CurrentCulture, $"{calls:N0} {(calls == 1 ? "call" : "calls")}")
                : ranked.Holds ? "0 completed" : "no calls";
        }

        return ranked.Value is { } value ? WorkspaceRowBuilder.DescribeSize(value)
            : ranked.Holds ? "unmeasured"
            : ranked.Metric == RankingMetric.BytesSent ? "no sends" : "no receives";
    }

    /// <summary>
    /// The value as a screen reader says it: the bytes with the records behind them and those that recorded no size, or
    /// the calls with those that failed and the stops paired with no start.
    /// </summary>
    public static string RankedSpoken(RankedValue ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Metric is RankingMetric.RpcCallsMade or RankingMetric.RpcCallsServed)
        {
            string side = ranked.Metric == RankingMetric.RpcCallsMade ? "made" : "served";
            string failed = ranked.Failed > 0 ? string.Create(CultureInfo.CurrentCulture, $", {ranked.Failed:N0} failed") : string.Empty;
            string unpaired = ranked.Unmeasured > 0
                ? string.Create(CultureInfo.CurrentCulture,
                    $", {ranked.Unmeasured:N0} {(ranked.Unmeasured == 1 ? "stop" : "stops")} paired with no start")
                : string.Empty;
            return ranked.Holds
                ? string.Create(CultureInfo.CurrentCulture,
                    $"{ranked.Measured:N0} RPC {(ranked.Measured == 1 ? "call" : "calls")} {side}{failed}{unpaired}")
                : $"no RPC calls {side}";
        }

        bool sent = ranked.Metric == RankingMetric.BytesSent;
        string records = sent ? "sends" : "receives";
        string unmeasured = ranked.Unmeasured > 0
            ? string.Create(CultureInfo.CurrentCulture, $", {ranked.Unmeasured:N0} {(ranked.Unmeasured == 1 ? records[..^1] : records)} with no size")
            : string.Empty;
        return ranked.Value is { } value
            ? string.Create(CultureInfo.CurrentCulture,
                $"{WorkspaceRowBuilder.DescribeSize(value)} {(sent ? "sent" : "received")} on {ranked.Measured:N0} measured {(ranked.Measured == 1 ? records[..^1] : records)}{unmeasured}")
            : ranked.Holds
                ? $"{(sent ? "bytes sent" : "bytes received")} unmeasured{unmeasured}"
                : $"no {records}";
    }

    public static IReadOnlyList<CrumbRow> Crumbs(DetailLadder ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        var crumbs = new List<CrumbRow>(ladder.Breadcrumb.Count);
        for (int depth = 0; depth < ladder.Breadcrumb.Count; depth++)
        {
            NavigationState rung = ladder.Breadcrumb[depth];
            crumbs.Add(new(depth, rung.Crumb, depth == ladder.Depth)
            {
                Display = rung.Level == DetailLevel.Channel && rung.Focus is { } channel
                    ? $"{NavigationState.Name(DetailLevel.Channel)}: {ChannelNames.Abbreviated(channel.Label)}"
                    : rung.Crumb,
            });
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
