using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One row of a rung's ranked table. It carries the level it descends into, so the same gesture works at
/// every rung and the table never needs a mode (section 3.2).
/// </summary>
public sealed record LadderRow(
    string Key,
    string Label,
    string Detail,
    long ObservationCount,
    long? KnownBytes,
    Mechanism Mechanism,
    CoverageState Coverage,
    DetailLevel DescendsTo,
    AccountingSide Side);

/// <summary>
/// What one rung shows. An empty rung names the source that would supply its data instead of showing an
/// empty pane, so the ladder has no dead ends (section 3.2, section 4.3).
/// </summary>
public sealed record LadderView
{
    public required NavigationState State { get; init; }
    public required string Breadcrumb { get; init; }
    public required IReadOnlyList<LadderRow> Rows { get; init; }

    /// <summary>Why this rung has no rows, naming what would supply them. Null when it has rows.</summary>
    public required string? EmptyReason { get; init; }

    /// <summary>
    /// How this rung's rows account for their observations. Under <c>CanonicalOwner</c> the rows partition
    /// the rung, so a total is a sum. Under <c>EndpointActivity</c> a channel is listed for both of its
    /// participants, so the rows overlap and their counts must not be added (section 5.1, section 5.3).
    /// </summary>
    public AccountingSide Side => Rows.Count == 0 ? AccountingSide.CanonicalOwner : Rows[0].Side;

    /// <summary>
    /// Observations this rung accounts for, or null when its rows overlap and no total is defined. An
    /// absent total is stated rather than replaced by a sum that double counts (section 5.1).
    /// </summary>
    public long? ObservationCount => Side == AccountingSide.CanonicalOwner
        ? Rows.Sum(row => row.ObservationCount)
        : null;

    /// <summary>Why this rung has no single total, when it has none.</summary>
    public string? TotalUnavailableReason => Side == AccountingSide.CanonicalOwner
        ? null
        : "A channel belongs to both of its participants, so these rows overlap and their counts are not "
            + "added. Descend to a channel for a total that is owned by one side.";

    /// <summary>Bytes this rung knows about. Rows without a byte value contribute nothing, not zero.</summary>
    public long? KnownBytes => Side == AccountingSide.CanonicalOwner && Rows.Any(row => row.KnownBytes.HasValue)
        ? Rows.Where(row => row.KnownBytes.HasValue).Sum(row => row.KnownBytes!.Value)
        : null;
}

/// <summary>
/// Projects a workspace snapshot at one rung of the ladder. It is a pure query: the same snapshot and
/// the same rung always produce the same rows, in the same order, which is what makes the ladder's
/// "same numbers" invariant testable (section 3.2, R13).
/// </summary>
public static class LadderProjection
{
    public static LadderView Project(WorkspaceSnapshot snapshot, NavigationState state)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);

        List<LadderRow> rows = state.Level switch
        {
            DetailLevel.Machine => Groups(snapshot),
            DetailLevel.Group => Processes(snapshot, state.Focus?.Key),
            DetailLevel.ProcessInstance => Channels(snapshot, state.Focus?.Key),
            DetailLevel.Channel => Operations(snapshot, state.Focus?.Key),
            DetailLevel.Operation => Evidence(snapshot, state.Focus?.Key),
            DetailLevel.Evidence => EvidenceDetail(snapshot, state.Focus?.Key),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        return new()
        {
            State = state,
            Breadcrumb = string.Join(" › ", Crumbs(state)),
            Rows = rows,
            EmptyReason = rows.Count > 0 ? null : EmptyReason(state),
        };
    }

    /// <summary>The breadcrumb for one rung in isolation; a ladder joins the crumbs of all its rungs.</summary>
    public static IReadOnlyList<string> Crumbs(NavigationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [state.Crumb];
    }

    /// <summary>The breadcrumb for a whole ladder, which is the position the user is never without.</summary>
    public static string Breadcrumb(DetailLadder ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return string.Join(" › ", ladder.Breadcrumb.Select(rung => rung.Crumb));
    }

    /// <summary>
    /// The descent a row implies, one rung down. The caller supplies the viewport it wants preserved,
    /// because the ladder restores a viewport rather than recomputing one.
    /// </summary>
    public static LadderDescent DescentFor(LadderRow row, NavigationState from, TimeRange viewport)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(from);
        return new()
        {
            Target = new(row.DescendsTo, row.Key, row.Label),
            Viewport = viewport,
            Lanes = LanesFor(row.DescendsTo),
            GraphFocusKey = row.DescendsTo <= DetailLevel.ProcessInstance ? row.Key : from.GraphFocusKey,
            AddedFilters = FiltersFor(row),
        };
    }

    /// <summary>
    /// The one-step descent to evidence that every rung must offer. It carries the rung's own scope as a
    /// visible filter, because reaching evidence from a channel is scoped by that channel.
    /// </summary>
    public static LadderDescent EvidenceDescentFor(NavigationState from, TimeRange viewport)
    {
        ArgumentNullException.ThrowIfNull(from);
        LadderTarget focus = from.Focus ?? new(from.Level, "machine", "Whole machine");
        return EvidenceDescentFor(from, viewport, focus,
            $"Evidence was reached from the {NavigationState.Name(from.Level).ToLowerInvariant()} rung.");
    }

    /// <summary>
    /// The evidence step for an explicitly named scope: a process selected at the machine rung, or a channel chosen
    /// in a discovery list the ladder does not hold. The scope is the visible, removable filter the step adds, so the
    /// jump is never an implicit predicate.
    /// </summary>
    public static LadderDescent EvidenceDescentFor(NavigationState from, TimeRange viewport, LadderTarget scope, string reason)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new()
        {
            Target = new(DetailLevel.Evidence, scope.Key, scope.Label),
            Viewport = viewport,
            Lanes = LaneGrouping.Endpoint,
            GraphFocusKey = from.GraphFocusKey,
            AddedFilters =
            [
                new("scope", scope.Label, reason)
                {
                    Key = scope.Level == DetailLevel.Machine ? null : scope.Key,
                    Level = scope.Level,
                },
            ],
        };
    }

    private static LaneGrouping LanesFor(DetailLevel level) => level switch
    {
        DetailLevel.Machine => LaneGrouping.Mechanism,
        DetailLevel.Group => LaneGrouping.Executable,
        DetailLevel.ProcessInstance => LaneGrouping.InstanceOnly,
        DetailLevel.Channel => LaneGrouping.Endpoint,
        DetailLevel.Operation => LaneGrouping.Endpoint,
        DetailLevel.Evidence => LaneGrouping.Endpoint,
        _ => LaneGrouping.InstanceOnly,
    };

    private static IReadOnlyList<ImpliedFilter> FiltersFor(LadderRow row) => row.DescendsTo switch
    {
        DetailLevel.Group => [new("group", row.Label, "Descending from the machine rung scopes to one group.")
            { Key = row.Key, Level = DetailLevel.Group }],
        DetailLevel.ProcessInstance =>
            [new("process", row.Label, "Descending from a group scopes to one process instance.")
                { Key = row.Key, Level = DetailLevel.ProcessInstance }],
        DetailLevel.Channel => [new("channel", row.Label, "Descending from a process scopes to one channel.")
            { Key = row.Key, Level = DetailLevel.Channel }],
        DetailLevel.Operation => [new("operation", row.Label, "Descending from a channel scopes to one operation.")
            { Key = row.Key, Level = DetailLevel.Operation }],
        DetailLevel.Evidence => [new("scope", row.Label, "Descending from an operation scopes to its records.")
            { Key = row.Key, Level = DetailLevel.Operation }],
        _ => [],
    };

    private static List<LadderRow> Groups(WorkspaceSnapshot snapshot)
    {
        var rows = new List<LadderRow>(snapshot.Groups.Count);
        foreach (ProcessGroup group in snapshot.Groups)
        {
            ProcessNode[] members =
            [
                .. snapshot.Processes.Where(process => string.Equals(process.GroupKey, group.Key, StringComparison.Ordinal)),
            ];
            CommunicationEdge[] edges = [.. OwnedBy(snapshot, members)];
            rows.Add(new(
                group.Key,
                group.Name,
                string.Create(CultureInfo.InvariantCulture, $"{members.Length} process instances"),
                edges.Sum(edge => edge.ObservationCount),
                KnownBytes(edges),
                Dominant(edges),
                Worst(members.Select(member => member.Coverage)),
                DetailLevel.Group,
                AccountingSide.CanonicalOwner));
        }

        return Rank(rows);
    }

    private static List<LadderRow> Processes(WorkspaceSnapshot snapshot, string? groupKey)
    {
        var rows = new List<LadderRow>();
        foreach (ProcessNode process in snapshot.Processes)
        {
            if (groupKey is not null && !string.Equals(process.GroupKey, groupKey, StringComparison.Ordinal))
            {
                continue;
            }

            CommunicationEdge[] edges = [.. OwnedBy(snapshot, [process])];
            rows.Add(new(
                process.Id.ToString(),
                process.Name,
                string.Create(CultureInfo.InvariantCulture, $"PID {process.ProcessId} · {process.Role}"),
                edges.Sum(edge => edge.ObservationCount),
                KnownBytes(edges),
                Dominant(edges),
                process.Coverage,
                DetailLevel.ProcessInstance,
                AccountingSide.CanonicalOwner));
        }

        return Rank(rows);
    }

    private static List<LadderRow> Channels(WorkspaceSnapshot snapshot, string? processKey)
    {
        var rows = new List<LadderRow>();
        foreach (Channel channel in snapshot.Channels)
        {
            CommunicationEdge? edge = snapshot.Edges.FirstOrDefault(
                candidate => string.Equals(candidate.Key, channel.EdgeKey, StringComparison.Ordinal));
            if (edge is null)
            {
                continue;
            }

            if (processKey is not null
                && !string.Equals(edge.SourceId.ToString(), processKey, StringComparison.Ordinal)
                && !string.Equals(edge.TargetId.ToString(), processKey, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new(
                channel.Key,
                channel.Name,
                channel.Direction == Direction.UnknownDirection
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{channel.Mechanism} · paired endpoints; direction varies by observation")
                    : string.Create(CultureInfo.InvariantCulture, $"{channel.Mechanism} · {channel.Direction}"),
                channel.ObservationCount,
                channel.KnownBytes,
                channel.Mechanism,
                channel.Coverage,
                DetailLevel.Channel,
                AccountingSide.EndpointActivity));
        }

        return Rank(rows);
    }

    private static List<LadderRow> Operations(WorkspaceSnapshot snapshot, string? channelKey)
    {
        var rows = new List<LadderRow>();
        foreach (ChannelOperation operation in snapshot.Operations)
        {
            if (channelKey is not null
                && !string.Equals(operation.ChannelKey, channelKey, StringComparison.Ordinal))
            {
                continue;
            }

            Channel? channel = snapshot.Channels.FirstOrDefault(
                candidate => string.Equals(candidate.Key, operation.ChannelKey, StringComparison.Ordinal));
            long records = snapshot.Evidence.Count(
                mark => string.Equals(mark.OperationKey, operation.Key, StringComparison.Ordinal));
            rows.Add(new(
                operation.Key,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{operation.Kind} {operation.Direction} · {operation.Interval.StartTicks / WorkspaceTime.TicksPerSecond} s"),
                Describe(operation),
                records,
                operation.CompletedBytes,
                channel?.Mechanism ?? Mechanism.UnknownMechanism,
                channel?.Coverage ?? CoverageState.UnknownCoverage,
                DetailLevel.Operation,
                AccountingSide.CanonicalOwner));
        }

        return Rank(rows);
    }

    private static List<LadderRow> Evidence(WorkspaceSnapshot snapshot, string? operationKey)
    {
        var rows = new List<LadderRow>();
        foreach (EvidenceMark mark in snapshot.Evidence)
        {
            if (operationKey is not null
                && !string.Equals(mark.OperationKey, operationKey, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(Row(snapshot, mark));
        }

        return rows;
    }

    /// <summary>
    /// The evidence rung reached in one step from anywhere. Its focus key may name a group, a process, a
    /// channel or an operation, so every scope is resolved rather than assumed to be an operation.
    /// </summary>
    private static List<LadderRow> EvidenceDetail(WorkspaceSnapshot snapshot, string? scopeKey)
    {
        if (scopeKey is null)
        {
            return [.. snapshot.Evidence.Select(mark => Row(snapshot, mark))];
        }

        HashSet<string> operations = ResolveOperationScope(snapshot, scopeKey);
        return
        [
            .. snapshot.Evidence
                .Where(mark => operations.Contains(mark.OperationKey))
                .Select(mark => Row(snapshot, mark)),
        ];
    }

    private static HashSet<string> ResolveOperationScope(WorkspaceSnapshot snapshot, string scopeKey)
    {
        if (snapshot.Operations.Any(operation => string.Equals(operation.Key, scopeKey, StringComparison.Ordinal)))
        {
            return [scopeKey];
        }

        HashSet<string> channels = ResolveChannelScope(snapshot, scopeKey);
        return
        [
            .. snapshot.Operations
                .Where(operation => channels.Contains(operation.ChannelKey))
                .Select(operation => operation.Key),
        ];
    }

    private static HashSet<string> ResolveChannelScope(WorkspaceSnapshot snapshot, string scopeKey)
    {
        if (snapshot.Channels.Any(channel => string.Equals(channel.Key, scopeKey, StringComparison.Ordinal)))
        {
            return [scopeKey];
        }

        HashSet<string> edges = ResolveEdgeScope(snapshot, scopeKey);
        return [.. snapshot.Channels.Where(channel => edges.Contains(channel.EdgeKey)).Select(channel => channel.Key)];
    }

    private static HashSet<string> ResolveEdgeScope(WorkspaceSnapshot snapshot, string scopeKey)
    {
        ProcessNode[] members =
        [
            .. snapshot.Processes.Where(process =>
                string.Equals(process.Id.ToString(), scopeKey, StringComparison.Ordinal)
                || string.Equals(process.GroupKey, scopeKey, StringComparison.Ordinal)),
        ];
        return members.Length == 0
            ? [.. snapshot.Edges.Select(edge => edge.Key)]
            : [.. EdgesOf(snapshot, members).Select(edge => edge.Key)];
    }

    private static LadderRow Row(WorkspaceSnapshot snapshot, EvidenceMark mark)
    {
        ChannelOperation? operation = snapshot.Operations.FirstOrDefault(
            candidate => string.Equals(candidate.Key, mark.OperationKey, StringComparison.Ordinal));
        Channel? channel = operation is null
            ? null
            : snapshot.Channels.FirstOrDefault(
                candidate => string.Equals(candidate.Key, operation.ChannelKey, StringComparison.Ordinal));
        return new(
            mark.Key,
            mark.Summary,
            string.Create(CultureInfo.InvariantCulture, $"{mark.Provider} · event {mark.EventId} · qpc {mark.NativeTicks}"),
            1,
            null,
            channel?.Mechanism ?? Mechanism.UnknownMechanism,
            channel?.Coverage ?? CoverageState.UnknownCoverage,
            DetailLevel.Evidence,
            AccountingSide.CanonicalOwner);
    }

    /// <summary>
    /// Requested and completed sizes stay separate and an absent one reads as unknown, never as zero
    /// (P3, R3). The text is kept short because a row's detail line is narrow; the full sentence lives in
    /// the row's accessible name.
    /// </summary>
    private static string Describe(ChannelOperation operation)
    {
        string requested = operation.RequestedBytes is { } value
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : "?";
        string completed = operation.CompletedBytes is { } done
            ? done.ToString("N0", CultureInfo.InvariantCulture)
            : "?";
        return operation.RequestedBytes is null && operation.CompletedBytes is null
            ? string.Create(CultureInfo.InvariantCulture, $"{operation.State} · no byte domain")
            : string.Create(CultureInfo.InvariantCulture, $"{operation.State} · {completed} of {requested} B");
    }

    /// <summary>
    /// A rung with nothing to show names the source that would supply it, because an empty pane is a dead
    /// end and the ladder has none (section 3.2, section 4.3).
    /// </summary>
    private static string EmptyReason(NavigationState state) => state.Level switch
    {
        DetailLevel.Machine =>
            "No process group has been observed. Process lifecycle events come from Microsoft-Windows-Kernel-Process.",
        DetailLevel.Group =>
            "This group has no observed process instance. Its members would come from Microsoft-Windows-Kernel-Process.",
        DetailLevel.ProcessInstance =>
            "This process has no observed channel. Channels come from the transport sources of section 4.1, "
            + "and a mechanism measured Unsupported contributes none.",
        DetailLevel.Channel =>
            "This channel has no paired operation. Pairing needs a start and its completion; an unpaired "
            + "start stays unresolved rather than being shown as an operation.",
        DetailLevel.Operation =>
            "This operation retained no source record. Records are kept by the admission policy of section "
            + "18.2, and a metadata-only policy keeps headers rather than bodies.",
        DetailLevel.Evidence =>
            "No source record is in scope. Widen the interval or remove a filter shown in the filter bar.",
        _ => "This rung has no data and no source is declared for it.",
    };

    /// <summary>
    /// Relationships this set canonically owns: the initiating side owns the relationship. Rows built from
    /// this partition the machine, which is what makes a parent total the sum of its children
    /// (<c>EN-AccountingSide</c>, section 3.2).
    /// </summary>
    private static IEnumerable<CommunicationEdge> OwnedBy(
        WorkspaceSnapshot snapshot,
        ProcessNode[] members)
    {
        HashSet<ProcessInstanceId> owners = [.. members.Select(member => member.Id)];
        return snapshot.Edges.Where(edge => owners.Contains(edge.SourceId));
    }

    /// <summary>
    /// Relationships this set participates in, either side. Used for scope, never for a total, because a
    /// relationship with both ends inside the set would otherwise be counted twice (section 5.1).
    /// </summary>
    private static IEnumerable<CommunicationEdge> EdgesOf(
        WorkspaceSnapshot snapshot,
        ProcessNode[] members)
    {
        HashSet<ProcessInstanceId> ids = [.. members.Select(member => member.Id)];
        return snapshot.Edges.Where(edge => ids.Contains(edge.SourceId) || ids.Contains(edge.TargetId));
    }

    private static long? KnownBytes(CommunicationEdge[] edges) =>
        edges.Any(edge => edge.KnownBytes.HasValue)
            ? edges.Where(edge => edge.KnownBytes.HasValue).Sum(edge => edge.KnownBytes!.Value)
            : null;

    private static Mechanism Dominant(CommunicationEdge[] edges) => edges.Length == 0
        ? Mechanism.UnknownMechanism
        : edges.OrderByDescending(edge => edge.ObservationCount).ThenBy(edge => edge.Key, StringComparer.Ordinal)
            .First().Mechanism;

    /// <summary>Coverage rolls up to its worst member, never to an average (<c>EN-CoverageState</c>).</summary>
    private static CoverageState Worst(IEnumerable<CoverageState> states)
    {
        CoverageState worst = CoverageState.Covered;
        foreach (CoverageState state in states)
        {
            if (state > worst)
            {
                worst = state;
            }
        }

        return worst;
    }

    /// <summary>Ranking is deterministic: observations first, then the key, never collection order (R13).</summary>
    private static List<LadderRow> Rank(List<LadderRow> rows)
    {
        rows.Sort(static (left, right) =>
        {
            int byCount = right.ObservationCount.CompareTo(left.ObservationCount);
            return byCount != 0 ? byCount : string.CompareOrdinal(left.Key, right.Key);
        });
        return rows;
    }
}
