using System.Globalization;
using Avalonia.Media;
using InterCat.Analysis;
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

    /// <summary>The rule, by identity and version, that derived the relationship (R4).</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>
    /// How the relationship was made, in words: its evidence, the rule that derived it, and what it rests on by key - what
    /// the evidence column's tooltip says, so no relationship is a mark nobody can explain (R4).
    /// </summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>The relationship's own key, by which Enter or a double click on the row opens it as its edge does.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>What opening the relationship shows, as its edge's card says a double click does: "its channel", say.</summary>
    public string Opens { get; init; } = string.Empty;

    public string AccessibleName =>
        $"{Source} to {Target} over {Mechanism}, {Spoken.Count(ObservationCount, "observation")}, {KnownBytes}, "
        + $"evidence {Evidence}, derived by {Rule}. Press Enter to open {Opens}.";
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
    /// Whether the row states bytes. A real session's timeline sums none per interval, so its rows state what was read for
    /// them, and a session with no directory to read from leaves the column out rather than calling every interval's
    /// bytes unknown.
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
                Rule = DescribeRule(edge.Rule),
                Explanation = Explain(edge),
                Key = edge.Key,
                Opens = Opens(snapshot, edge),
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
    /// timeline sums no bytes (<paramref name="sumsBytes"/> false), the rows state none; where they are read apart from it
    /// (<paramref name="bytesOf"/>), each row states what was read for its interval.
    /// </summary>
    public static IReadOnlyList<IntervalRow> Intervals(
        IReadOnlyList<TimelineBucket> buckets, ThemeMode mode, IReadOnlyList<TimelineBucket>? focus = null,
        bool sumsBytes = true, Func<TimelineBucket, string>? bytesOf = null)
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
                bytesOf is null ? DescribeBytes(bucket.KnownBytes) : bytesOf(bucket),
                tokens.Label,
                tokens.Glyph,
                CoverageStateText.Label(bucket.Coverage))
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
    /// What a set of records sent and received, as an interval's row states it: each side's measured sum, how many of its
    /// records recorded no size, or that none was recorded (R3, R21). It speaks of records, never of the machine: beside an
    /// interval of unknown coverage, "no receive recorded" is true where "nothing received" would claim what was not
    /// observed (§10.3). Records that state neither side follow, where there are any.
    /// </summary>
    public static string DescribeTransfers(TransportBytes bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bool sides = bytes.SentMeasured + bytes.SentUnmeasured + bytes.ReceivedMeasured + bytes.ReceivedUnmeasured > 0;
        bool unsided = bytes.OtherMeasured + bytes.OtherUnmeasured > 0;
        if (!sides && !unsided)
        {
            return "no transfer recorded";
        }

        string text = Directional(bytes.SentBytes, bytes.SentMeasured, bytes.SentUnmeasured, "sent", "send")
            + " · " + Directional(bytes.ReceivedBytes, bytes.ReceivedMeasured, bytes.ReceivedUnmeasured, "received", "receive");
        return !unsided ? text
            : text + " · " + (bytes.OtherMeasured > 0
                ? DescribeSize(bytes.OtherBytes) + " with no side stated"
                    + (bytes.OtherUnmeasured > 0
                        ? string.Create(CultureInfo.CurrentCulture, $" ({bytes.OtherUnmeasured:N0} unmeasured)")
                        : string.Empty)
                : string.Create(CultureInfo.CurrentCulture, $"{bytes.OtherUnmeasured:N0} unmeasured with no side stated"));
    }

    /// <summary>
    /// One direction's bytes in words, as a one-sided connection's are said (R5): a measured sum, with how many of its
    /// records stated no size; only how many stated none, where none measured one; or, with no record that way, "no send
    /// recorded" - of the records, never a claim that nothing was sent (R21, §10.3).
    /// </summary>
    public static string Directional(long value, long measured, long unmeasured, string verb, string noun) =>
        measured > 0
            ? DescribeSize(value) + " " + verb + (unmeasured > 0 ? $" ({CountText.Of(unmeasured, noun)} unmeasured)" : string.Empty)
            : unmeasured > 0 ? $"{CountText.Of(unmeasured, noun)} unmeasured"
            : $"no {noun} recorded";

    /// <summary>
    /// A per-second value as the ranked table shows it: three significant figures and at most three decimals (§1.4),
    /// "214", "12.3", "4.20", "0.051"; a value too small to show is "< 0.001", never a zero it is not.
    /// </summary>
    public static string DescribeRate(double perSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perSecond);
        if (perSecond == 0)
        {
            return "0";
        }

        if (perSecond < 0.0005)
        {
            return "< 0.001";
        }

        string format = perSecond >= 99.95 ? "N0" : perSecond >= 9.995 ? "N1" : perSecond >= 0.9995 ? "N2" : "N3";
        return perSecond.ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// A byte rate in <see cref="DescribeSize"/>'s decimal units per second: "147 KB/s", "830 B/s", "0.42 B/s" (§1.4:
    /// a decimal byte rate is written MB/s).
    /// </summary>
    public static string DescribeByteRate(double bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        return bytesPerSecond >= 999.5
            ? DescribeSize((long)Math.Round(bytesPerSecond, MidpointRounding.ToEven)) + "/s"
            : DescribeRate(bytesPerSecond) + " B/s";
    }

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

    /// <summary>A rule as a person reads it: "transport-endpoint-relation, version 4".</summary>
    public static string DescribeRule(RelationRule rule) => ProcessBindingText.Rule(rule);

    /// <summary>
    /// How a relationship was made, in words (R4): its evidence, the rule and version that derived it, and what it rests on
    /// by key - the first of its channels, each of which opens its records, and how many more there are; or, for an RPC
    /// relationship, its own key, which opens the calls its links join.
    /// </summary>
    public static string Explain(CommunicationEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        const int named = 3;
        string strength = DescribeStrength(edge.Strength);
        string keys = string.Join(", ", edge.Evidence.Take(named))
            + (edge.Evidence.Count > named ? string.Create(CultureInfo.CurrentCulture, $", and {edge.Evidence.Count - named:N0} more") : string.Empty);
        string rests = edge.Evidence.Count == 0 ? "no evidence this overview keeps"
            : edge.Rule == RelationRule.RpcCallPeer ? "the calls its links join: " + keys
            : Spoken.Count(edge.Evidence.Count, "channel") + ": " + keys;
        return $"{char.ToUpperInvariant(strength[0])}{strength[1..]} evidence, derived by {DescribeRule(edge.Rule)}, from {rests}.";
    }

    /// <summary>
    /// What opening a relationship shows, from its edge or its row in the table (§6.7, R15): the channel it rests on when
    /// it rests on one, the calls its links join when calls link it, or else its source process, whose rows list its
    /// channels.
    /// </summary>
    public static string Opens(WorkspaceSnapshot snapshot, CommunicationEdge edge)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(edge);
        if (edge.Rule == RelationRule.RpcCallPeer)
        {
            return "the calls its links join";
        }

        int channels = snapshot.Channels.Count(channel => string.Equals(channel.EdgeKey, edge.Key, StringComparison.Ordinal));
        return channels switch
        {
            1 => "its channel",
            0 => "its source process",
            _ => string.Create(CultureInfo.CurrentCulture, $"its source process, whose rows list its {channels:N0} channels"),
        };
    }

    /// <summary>
    /// How a process's own records were bound to it over the session, in words (§6.8: a ranked row or node is one action
    /// from the rule, version and coverage behind its count): the rule that bound them and how strongly, what the
    /// evidence policy left out of its total, and the capture's coverage. A later holder of a reused PID binds its records
    /// only as candidates, which a policy that admits none counts in no process; the explanation says so, and how many,
    /// so its row does not read as a quiet process (R21, R22).
    /// </summary>
    public static string ExplainBinding(ProcessNode process) => ProcessBindingText.Explain(process);

    /// <summary>
    /// Why a reused PID's later holder's rung lists no row while the evidence policy counts no candidate (§6.8), in the
    /// words its binding explanation uses: every record naming its PID while it ran could be an earlier holder's, so none
    /// is counted as its own, and only its lifecycle records are one step away.
    /// </summary>
    public static string ExplainWithheldRung(ProcessNode process)
    {
        ArgumentNullException.ThrowIfNull(process);
        long withheld = process.WithheldRecords;
        return string.Create(CultureInfo.InvariantCulture, $"This process lists no row: PID {process.ProcessId} was held by an earlier ")
            + $"process in this capture, so the {Spoken.Count(withheld, "record")} bound to this one over the session "
            + (withheld == 1 ? "is only a candidate" : "are only candidates")
            + ", which the evidence policy does not count. Its lifecycle records are one step away.";
    }

    /// <summary>
    /// How a group was formed and counted, in words (§6.8: a group row is one action from what its total rests on): by the
    /// executable its members' records name, or the terminal session their lifecycle records name, or together when none
    /// names one, the rule that bound each member's own records, what the evidence policy left out of its total, and the
    /// worst coverage among its members.
    /// </summary>
    public static string ExplainGrouping(ProcessGroup group, IReadOnlyCollection<ProcessNode> members)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        string rule = DescribeRule(RelationRule.Parse(ProcessInstanceIndex.BindingRule));
        string grouped = group.Kind == LaneGrouping.UserSession
            ? group.Key == WorkspaceGrouping.UnrecordedSessionKey
                ? "Grouped here because no lifecycle record names the terminal session its members ran in: none is "
                    + "given a guessed one."
                : "Grouped by the terminal session its members' lifecycle records name."
            : group.Key == ProcessGroup.UnwitnessedExecutableKey
            ? "Grouped here because no record names the executable its members ran: none is given a guessed name."
            : "Grouped by the executable its members' records name, compared without case.";
        long withheld = members.Sum(member => member.WithheldRecords);
        long candidates = members.Sum(member => member.CandidateRecords);
        string left = withheld > 0
            ? $" The evidence policy counts no candidate, so its total leaves out {Spoken.Count(withheld, "record")} bound to "
                + $"{LaterHolders(members.Count(member => member.WithheldRecords > 0))} among them."
            : candidates > 0
            ? $" The evidence policy counts candidates, so its total includes {Spoken.Count(candidates, "record")} bound to "
                + $"{LaterHolders(members.Count(member => member.CandidateRecords > 0))} among them."
            : string.Empty;
        CoverageState coverage = members.Count == 0 ? CoverageState.UnknownCoverage : members.Max(member => member.Coverage);
        return $"{grouped} Each member's own records are bound to it by {rule}.{left} "
            + $"Coverage over the session: {CoverageWords(coverage)}.";
    }

    private static string LaterHolders(int count) =>
        Spoken.Count(count, "later holder of a reused PID", "later holders of reused PIDs");

    /// <summary>
    /// How a paired channel's two ends were paired, in words (§6.8: a channel row is one action from the rule, version,
    /// evidence key and coverage behind it): the rule and how strongly the pairing holds, whether the capture saw the
    /// connection open and close, the capture's coverage over the session, and the key E lists its records by.
    /// </summary>
    public static string ExplainPairing(Channel channel) =>
        // The key comes last: it is what icat evidence --channel takes, and long enough to bury the sentence before it.
        ChannelPairingText.Explain(channel) + $" E lists its records at both ends, by its key {channel.Key}.";

    /// <summary>A coverage state as a sentence ends with it: "covered", "partial gap, not extrapolated", "unknown".</summary>
    private static string CoverageWords(CoverageState coverage) => CoverageStateText.Value(coverage);

    private static string DescribeStrength(RelationStrength strength) => strength switch
    {
        RelationStrength.Direct => "direct",
        RelationStrength.Correlated => "correlated",
        RelationStrength.Candidate => "candidate",
        RelationStrength.Unresolved => "unresolved",
        _ => "conflicting",
    };
}
