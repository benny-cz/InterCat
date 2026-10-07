using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>A node a person placed on a session's graph: its stable graph key, and where they put it (§19.4).</summary>
public sealed record WorkspacePin
{
    /// <summary>The node's stable key: a process instance, a group or an aggregate, as the graph names it.</summary>
    public required string Key { get; init; }

    /// <summary>Where across the graph, from 0 to 1.</summary>
    public required double X { get; init; }

    /// <summary>Where down the graph, from 0 to 1.</summary>
    public required double Y { get; init; }
}

/// <summary>
/// How a person laid out a member session's view (§26.3's workspace scope): the nodes they pinned on its graph, where, the
/// process lanes they pinned on its timeline, what its rows are ranked by, the evidence policy its records are counted
/// under, and the scale its timeline lanes are read against. It is a preference, not a finding, so a member has one,
/// replaced as it changes rather than kept as revisions.
/// </summary>
public sealed record WorkspaceLayout
{
    public required Guid SessionId { get; init; }

    /// <summary>The pinned nodes, by key.</summary>
    public required IReadOnlyList<WorkspacePin> Pins { get; init; }

    /// <summary>
    /// The process instances whose timeline lanes are pinned at the top of their group's lanes (§6.2), in the order pinned;
    /// empty when none is. A file of a version before 16 pins none.
    /// </summary>
    public IReadOnlyList<Guid> PinnedLanes { get; init; } = [];

    /// <summary>What the session's rows are ranked by (§6.1) when not by their own records; null ranks by records.</summary>
    public RankingMetric? RankBy { get; init; }

    /// <summary>Whether a count or sum the rows are ranked by reads per second of the ranked interval.</summary>
    public bool PerSecond { get; init; }

    /// <summary>
    /// How strongly a record must bind to a process to count as that process's in the session's view (§6.8), when not
    /// correlated evidence: candidates too, the one other policy a view offers. Null counts correlated evidence, the default.
    /// </summary>
    public EvidencePolicy? EvidencePolicy { get; init; }

    /// <summary>
    /// Whether each of the session's timeline lanes is read against its own busiest bar rather than one scale every lane
    /// shares, the default (§6.2's normalization scope).
    /// </summary>
    public bool ScalesEachLane { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    /// Whether it keeps anything: a pinned node or lane, a ranking other than records counted whole, another evidence
    /// policy, or each lane on its own scale.
    /// </summary>
    [JsonIgnore]
    public bool KeepsAnything => Pins.Count > 0 || PinnedLanes.Count > 0 || RankBy is not null || PerSecond
        || EvidencePolicy is not null || ScalesEachLane;

    /// <summary>
    /// What it keeps, in the words every place that says so uses - `icat workspace show`, the notice of a session opened
    /// from its investigation and that investigation's window: "1 node pinned on its graph and its rows ranked by bytes
    /// sent per second". Empty when it keeps nothing.
    /// </summary>
    public string Describe(IFormatProvider culture) => Describe(Pins.Count, PinnedLanes.Count, RankBy ?? RankingMetric.Records,
        PerSecond, EvidencePolicy ?? Domain.EvidencePolicy.IncludeCorrelated, ScalesEachLane, culture);

    /// <summary>
    /// What a layout that pins <paramref name="pins"/> nodes and <paramref name="lanes"/> process lanes, ranks by
    /// <paramref name="rankBy"/>, counts under <paramref name="evidencePolicy"/> and reads each lane on its own scale or not
    /// keeps, in <see cref="Describe(IFormatProvider)"/>'s words: nothing of a default, so what is said is only what differs
    /// from a session opened on its own.
    /// </summary>
    public static string Describe(int pins, int lanes, RankingMetric rankBy, bool perSecond, EvidencePolicy evidencePolicy,
        bool scalesEachLane, IFormatProvider culture) =>
        Series(Parts(pins, lanes, rankBy, perSecond, evidencePolicy, scalesEachLane, culture));

    /// <summary>
    /// Each thing <see cref="Describe(int, int, RankingMetric, bool, EvidencePolicy, bool, IFormatProvider)"/> says such a
    /// layout keeps, in its order, so a notice can list it in one series with what else was put back.
    /// </summary>
    public static IReadOnlyList<string> Parts(int pins, int lanes, RankingMetric rankBy, bool perSecond,
        EvidencePolicy evidencePolicy, bool scalesEachLane, IFormatProvider culture) =>
    [
        .. new[]
        {
            pins == 0 ? null : string.Create(culture, $"{pins:N0} {(pins == 1 ? "node" : "nodes")} pinned on its graph"),
            lanes == 0
                ? null
                : string.Create(culture, $"{lanes:N0} {(lanes == 1 ? "process lane" : "process lanes")} pinned on its timeline"),
            rankBy == RankingMetric.Records && !perSecond
                ? null
                : $"its rows ranked by {RankingMetrics.Phrase(rankBy)}{(perSecond ? " per second" : string.Empty)}",
            evidencePolicy == Domain.EvidencePolicy.IncludeCandidates ? "its records counted with candidates" : null,
            scalesEachLane ? "each of its timeline lanes on its own scale" : null,
        }.OfType<string>(),
    ];

    /// <summary>Things said as one series: one alone, two joined by "and", more listed with "and" before the last.</summary>
    public static string Series(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Count < 2 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The most nodes one member's layout pins.</summary>
    public const int MostPins = 1_024;

    /// <summary>The longest a pinned node's key may be, in characters.</summary>
    public const int MostPinKeyCharacters = 256;

    /// <summary>The most process lanes one member's layout pins.</summary>
    public const int MostPinnedLanes = 1_024;

    /// <summary>
    /// Keeps <paramref name="pins"/>, the ranking <paramref name="rankBy"/> read per second when
    /// <paramref name="perSecond"/> says so, the records counted under <paramref name="evidencePolicy"/>, each timeline
    /// lane read against its own peak when <paramref name="scalesEachLane"/> says so, and the process lanes
    /// <paramref name="pinnedLanes"/> pinned in their order, as how member <paramref name="sessionId"/>'s view is laid out,
    /// replacing its earlier layout; one that pins nothing, ranks by records counted whole, counts correlated evidence and
    /// reads every lane on one scale removes it. Null when it is removed.
    /// </summary>
    public static WorkspaceLayout? SetLayout(
        string workspacePath,
        Guid sessionId,
        IReadOnlyList<WorkspacePin> pins,
        DateTimeOffset now,
        RankingMetric rankBy = RankingMetric.Records,
        bool perSecond = false,
        EvidencePolicy evidencePolicy = Domain.EvidencePolicy.IncludeCorrelated,
        bool scalesEachLane = false,
        IReadOnlyList<Guid>? pinnedLanes = null)
    {
        ArgumentNullException.ThrowIfNull(pins);
        pinnedLanes ??= [];
        if (!Enum.IsDefined(rankBy))
        {
            throw new InvalidOperationException("The layout is refused: it ranks by no metric §6.1 offers.");
        }

        if (evidencePolicy is not (Domain.EvidencePolicy.IncludeCorrelated or Domain.EvidencePolicy.IncludeCandidates))
        {
            throw new InvalidOperationException(
                "The layout is refused: a view counts correlated evidence, or candidates too, and under no other policy.");
        }

        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        if (workspace.Members.All(member => member.SessionId != sessionId))
        {
            throw new InvalidOperationException($"Session {sessionId:N} is not a member of this investigation, so it keeps no layout here.");
        }

        if ((PinsProblem(pins) ?? LanesProblem(pinnedLanes)) is { } problem)
        {
            throw new InvalidOperationException($"The layout is refused: {problem}.");
        }

        var kept = new WorkspaceLayout
        {
            SessionId = sessionId,
            Pins = [.. pins.OrderBy(pin => pin.Key, StringComparer.Ordinal)],
            PinnedLanes = [.. pinnedLanes],
            RankBy = rankBy == RankingMetric.Records ? null : rankBy,
            PerSecond = perSecond,
            EvidencePolicy = evidencePolicy == Domain.EvidencePolicy.IncludeCorrelated ? null : evidencePolicy,
            ScalesEachLane = scalesEachLane,
            UpdatedUtc = now,
        };
        WorkspaceLayout? layout = kept.KeepsAnything ? kept : null;
        Save(full, workspace with
        {
            Layouts = [.. workspace.Layouts.Where(kept => kept.SessionId != sessionId), .. layout is null ? [] : new[] { layout }],
            UpdatedUtc = now,
        }, file);
        return layout;
    }

    /// <summary>How member <paramref name="sessionId"/>'s view is laid out, or null when it keeps nothing here.</summary>
    public static WorkspaceLayout? LayoutOf(InvestigationWorkspaceFile workspace, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Layouts.FirstOrDefault(layout => layout.SessionId == sessionId);
    }

    private static string? PinsProblem(IReadOnlyList<WorkspacePin> pins) =>
        pins.Count > MostPins ? $"it pins more than {MostPins:N0} nodes"
        : pins.Any(pin => pin is null || string.IsNullOrWhiteSpace(pin.Key) || pin.Key.Length > MostPinKeyCharacters)
            ? $"a pinned node has no key, or one beyond {MostPinKeyCharacters} characters"
        : pins.GroupBy(pin => pin.Key, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "a node is pinned twice"
        : pins.Any(pin => !new GraphPoint(pin.X, pin.Y).IsValid) ? "a node is pinned outside the graph"
        : null;

    /// <summary>What makes a layout's pinned lanes ones no window pins, or null: too many, the empty identity, or one twice.</summary>
    private static string? LanesProblem(IReadOnlyList<Guid> lanes) =>
        lanes.Count > MostPinnedLanes ? $"it pins more than {MostPinnedLanes:N0} lanes"
        : lanes.Contains(Guid.Empty) ? "a lane is pinned by no process instance"
        : lanes.Distinct().Count() != lanes.Count ? "a lane is pinned twice"
        : null;

    /// <summary>What makes a file's layouts contradict themselves, or null (`contracts/workspace-v16.md` §7).</summary>
    private static string? LayoutProblem(InvestigationWorkspaceFile workspace)
    {
        // Layouts arrived with the eleventh version (revision 280).
        if (workspace.Layouts.Count > 0 && VersionOf(workspace) < 11)
        {
            return $"a {workspace.Contract} file holds no layout";
        }

        if (workspace.Layouts.Any(layout => layout is null || layout.Pins is null))
        {
            return "it lists an empty layout";
        }

        // A layout's ranking arrived with the twelfth version (revision 301).
        if (VersionOf(workspace) < 12 && workspace.Layouts.Any(layout => layout.RankBy is not null || layout.PerSecond))
        {
            return $"a {workspace.Contract} file ranks no session's rows";
        }

        // A layout's evidence policy arrived with the thirteenth version (revision 339).
        if (VersionOf(workspace) < 13 && workspace.Layouts.Any(layout => layout.EvidencePolicy is not null))
        {
            return $"a {workspace.Contract} file counts no session's records under another evidence policy";
        }

        // Each lane on its own scale arrived with the fourteenth version (revision 343).
        if (VersionOf(workspace) < 14 && workspace.Layouts.Any(layout => layout.ScalesEachLane))
        {
            return $"a {workspace.Contract} file reads no session's timeline lanes on scales of their own";
        }

        // Pinned lanes arrived with the sixteenth version (revision 388).
        if (VersionOf(workspace) < 16 && workspace.Layouts.Any(layout => layout.PinnedLanes.Count > 0))
        {
            return $"a {workspace.Contract} file pins no session's timeline lanes";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        if (workspace.Layouts.GroupBy(layout => layout.SessionId).FirstOrDefault(group => group.Count() > 1) is { } twice)
        {
            return $"session {twice.Key:N} has two layouts";
        }

        // Records are what no ranking ranks by, so a layout names another metric or none; correlated evidence is what every
        // view counts unless told otherwise, and candidates the one other policy a view offers, so a layout names them or
        // none: a reader puts back no policy it cannot show.
        return workspace.Layouts.FirstOrDefault(layout => !members.Contains(layout.SessionId) || !layout.KeepsAnything
            || layout.RankBy is { } rankBy && (rankBy == RankingMetric.Records || !Enum.IsDefined(rankBy))
            || layout.EvidencePolicy is not (null or Domain.EvidencePolicy.IncludeCandidates)
            || PinsProblem(layout.Pins) is not null || LanesProblem(layout.PinnedLanes) is not null) is { } wrong
            ? $"the layout of session {wrong.SessionId:N} "
                + (!members.Contains(wrong.SessionId) ? "is of no member"
                    : !wrong.KeepsAnything ? "keeps nothing"
                    : (PinsProblem(wrong.Pins) ?? LanesProblem(wrong.PinnedLanes)) is { } problem ? "is refused: " + problem
                    : wrong.EvidencePolicy is not (null or Domain.EvidencePolicy.IncludeCandidates)
                        ? "counts its records under a policy no view offers, or under correlated evidence by name"
                    : "ranks by no metric §6.1 offers, or by records by name")
            : null;
    }
}
