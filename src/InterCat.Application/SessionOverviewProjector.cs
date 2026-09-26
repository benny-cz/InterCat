using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One immutable, evidence-bounded overview. Its edges are only paired TCP relationships whose two process instances
/// are admitted under the requested policy; they are not a total of all traffic. The timeline counts all observations
/// with a usable session time and projects capture coverage only when this generation publishes a ledger. The graph's
/// own counts are the displayed relations' records, a narrower scope that never stands in for the timeline's.
/// </summary>
public sealed record SessionOverviewBundle(
    string GraphIdentity,
    Guid SessionId,
    long Generation,
    TimeRange? Extent,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ProcessNode> Nodes,
    IReadOnlyList<CommunicationEdge> Edges,
    IReadOnlyList<Channel> Channels,
    string? ChannelProjectionProblem,
    IReadOnlyList<TimelineBucket> Timeline,
    long ObservationRows,
    long RowsWithoutSessionTime,
    long GraphEligibleRows,
    long GraphRowsWithoutSessionTime,
    long UnresolvedTcpRows,
    long RelationshipsNotAdmitted,
    bool CoverageLedgerPublished,
    IReadOnlyList<MechanismCoverage> MechanismCoverage,
    IReadOnlyList<string> Caveats,
    SessionMinimap? Minimap = null,
    SessionRedaction? Redaction = null)
{
    public IReadOnlyList<MechanismTimelineLane> MechanismLanes { get; init; } = [];

    /// <summary>
    /// The clock this generation's readings are on, which places anything else read on it - such as a live preview's
    /// bins - on the same presentation axis as the timeline.
    /// </summary>
    public SourceClockDescriptor? Clock { get; init; }
}

/// <summary>
/// Builds graph and timeline data from exactly one leased generation. This is the read-side bundle for IC-017, not a
/// full ladder projection: logical operations and exact-record drill-down remain separate derivations. A caller must not
/// advertise its resolved-relationship edge count as the session's total observations or byte volume.
/// </summary>
public static class SessionOverviewProjector
{
    public const int MaximumTimelineBuckets = 64;
    public const int MaximumChannels = 4_096;

    public static SessionOverviewBundle Project(
        SessionStore store,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        int maximumChannels = MaximumChannels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChannels);

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException(
                "This generation names no source clock. Process lifetimes and the overview time axis cannot be "
                + "derived from an assumed clock.");
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest)
            .Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest)
            .Select(name => SessionSegments.Open(store, manifest, name))];
        CoverageLedgerV1? coverage = SessionSegments.CoverageLedger(store.Root, manifest);
        SessionRedaction? redaction = SessionRedaction.Read(store.Root, manifest);
        SessionDerivation derivation = SessionDerivationCache.For(manifest);
        TransportRelationIndex relations = derivation.Relations(segments, clock, fields, cancellationToken);
        ProcessInstanceIndex processes = derivation.Processes(segments, clock, fields, cancellationToken);

        // Every instance and relationship is in the bundle, however many there are. What the graph draws at once is the
        // display projection's bound (GraphProjection, §6.3): it clusters rather than omitting anyone.
        ProcessInstance[] ordered = [.. processes.Instances.OrderBy(instance => instance.Id.ToString(), StringComparer.Ordinal)];
        IGrouping<string, ProcessInstance>[] executables = [.. ordered
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)];
        Dictionary<string, string> names = ExecutableNames.Label(
            [.. executables.Select(group => (group.Key, group.First().ImagePath))]);
        ProcessGroup[] groups = [.. executables.Select(group => new ProcessGroup(
            group.Key, names[group.Key], LaneGrouping.Executable, GroupPath(group.First())))];
        ProcessNode[] nodes = [.. ordered.Select((instance, index) => new ProcessNode(
            instance.Id,
            instance.ProcessId,
            instance.ImageName ?? ProcessNode.PidName(instance.ProcessId),
            instance.Witness.ToString(),
            GroupKey(instance),
            0.5 + 0.38 * Math.Cos(2 * Math.PI * index / Math.Max(1, ordered.Length)),
            0.5 + 0.38 * Math.Sin(2 * Math.PI * index / Math.Max(1, ordered.Length)),
            CoverageState.UnknownCoverage))];

        // The overview graph shows TCP relationships; UDP datagram flows are related too, and join the graph when its edges
        // and timeline carry a mechanism of their own rather than a TCP label.
        TransportRelation[] tcp = [.. relations.Relations.Where(relation => relation.Mechanism == Mechanism.Tcp)];
        TransportRelation[] admitted = [.. tcp.Where(relation => Admitted(relation.Strength, policy))];
        long notAdmitted = tcp.Length - admitted.Length;
        CommunicationEdge[] edges = [.. admitted
            .GroupBy(relation => Pair(relation.First.Id, relation.Second.Id))
            .OrderBy(group => group.Key.First.ToString(), StringComparer.Ordinal)
            .ThenBy(group => group.Key.Second.ToString(), StringComparer.Ordinal)
            .Select(group => new CommunicationEdge(
                $"tcp:{group.Key.First}:{group.Key.Second}",
                group.Key.First,
                group.Key.Second,
                Mechanism.Tcp,
                group.Sum(relation => relation.Records),
                null,
                group.Max(relation => relation.Strength)))];

        string? channelProblem = admitted.Length > maximumChannels
            ? $"This generation has {admitted.Length:N0} admitted paired TCP channels, above the "
                + $"{maximumChannels:N0}-channel overview bound. The graph and timeline remain available; "
                + "the channel rung needs a scoped query before it can show the complete set. "
                + "Use icat channels <session-directory> --process <instance-guid> to page admitted channels."
            : null;
        Channel[] channels = channelProblem is not null ? [] : [.. admitted
            .OrderBy(relation => relation.StableKey, StringComparer.Ordinal)
            .Select(ProjectChannel)];
        if (channelProblem is null
            && channels.Select(channel => channel.Key).Distinct(StringComparer.Ordinal).Count() != channels.Length)
        {
            throw new InvalidDataException(
                "Two admitted TCP incarnations have the same raw-fact channel key. Refusing to merge them.");
        }

        int unrelated = nodes.Length - edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Distinct().Count();

        // What the graph draws is counted from the relations themselves: a relation holds exactly the records at its two
        // ends, and every record of an incarnation has that incarnation's other end, so no row is looked up again here.
        long graphRows = admitted.Sum(relation => relation.Records);
        long graphWithoutTime = admitted.Sum(relation => relation.RecordsWithoutSessionTime);
        long unresolved = relations.RecordsWithoutAdmittedPeer(Mechanism.Tcp, policy);
        (TimeRange? extent, TimelineBucket[] timeline, MechanismTimelineLane[] lanes, SessionMinimap? minimap,
            long rows, long withoutTime) = Timeline(segments, clock, coverage, cancellationToken);
        string[] caveats =
        [
            "Graph edges show paired TCP connection incarnations whose two process instances are admitted. "
                + "One-sided, ambiguous and unsupported relationships are not rendered as guessed edges.",
            "Edge direction is a stable display order, not a claim about which process initiated or sent data. "
                + "Edge record counts include observations from both ends and are not whole-session totals.",
            coverage is null
                ? "The timeline counts observed rows only. This legacy generation publishes no coverage ledger, "
                    + "so its coverage is unknown; an empty interval is not proof of inactivity."
                : "Timeline coverage describes the captured mechanisms of observed rows within the ledger's "
                    + "delivered readings. Empty buckets stay unknown, and source loss has no finer location "
                    + "than its epoch. It does not prove a graph relationship or an empty interval complete.",
            $"{withoutTime:N0} {(withoutTime == 1 ? "row has" : "rows have")} no usable session time and "
                + $"{(withoutTime == 1 ? "is" : "are")} absent from the timeline; "
                + $"{unresolved:N0} TCP rows have no admitted peer; {notAdmitted:N0} paired relationships "
                + "were withheld by the evidence policy.",
            $"{graphRows:N0} rows belong to displayed graph edges; {graphWithoutTime:N0} of them have no usable "
                + "session time and are among the rows absent from the timeline.",
            "Byte totals, logical operations and exact-record drill-down are not in this overview bundle. "
                + "Channels name only admitted paired TCP incarnations; one-sided or ambiguous transport activity "
                + "remains in the all-observations timeline, not a guessed channel.",
            .. (channelProblem is null ? [] : new[] { channelProblem }),
            .. (unrelated < 2 && nodes.Length <= GraphDisplayBudget.Default.Nodes
                && edges.Length <= GraphDisplayBudget.Default.Edges
                ? []
                : new[]
                {
                    $"The Desktop graph draws relationships, not every process: {unrelated:N0} of {nodes.Length:N0} "
                        + "process instances have no admitted relationship and are counted in one node, and a graph above "
                        + $"{GraphDisplayBudget.Default.Nodes:N0} nodes or {GraphDisplayBudget.Default.Edges:N0} edges "
                        + "collapses groups, lowest activity first, with explicit Other-members/remainder fallbacks. No "
                        + "process is omitted: every instance stays individual in the ranked table and every source "
                        + "relationship remains addressable.",
                }),
            .. (redaction is null ? [] : new[] { SessionRedaction.Summary + " " + redaction.Warning }),
        ];
        return new SessionOverviewBundle(
            $"session:{manifest.SessionId:N}:generation:{manifest.Generation}:digest:{manifest.Digest}"
                + $":relation:{TransportRelationIndex.RelationRule}:policy:{policy}",
            manifest.SessionId,
            manifest.Generation,
            extent,
            Array.AsReadOnly(groups),
            Array.AsReadOnly(nodes),
            Array.AsReadOnly(edges),
            Array.AsReadOnly(channels),
            channelProblem,
            Array.AsReadOnly(timeline),
            rows,
            withoutTime,
            graphRows,
            graphWithoutTime,
            unresolved,
            notAdmitted,
            coverage is not null,
            Array.AsReadOnly([.. SessionCoverage.ByMechanism(coverage)]),
            Array.AsReadOnly(caveats),
            minimap,
            redaction)
        {
            MechanismLanes = Array.AsReadOnly(lanes),
            Clock = clock,
        };
    }

    private static (TimeRange? Extent, TimelineBucket[] Buckets, MechanismTimelineLane[] Lanes, SessionMinimap? Minimap,
        long Rows, long WithoutTime)
        Timeline(
            SegmentReaderV1[] segments,
            SourceClockDescriptor clock,
            CoverageLedgerV1? coverage,
            CancellationToken cancellationToken)
    {
        // Every count comes from the segments' tiles (§12.1 S4's first level), built once per reader: the extent from
        // their earliest and latest readings, and the buckets and minimap columns from tiles whole wherever a tile's
        // records fall in one column. Only a tile a bucket boundary crosses has its rows read (§10.3).
        long rows = 0;
        long withoutTime = 0;
        long minimum = long.MaxValue;
        long maximum = long.MinValue;
        var tiles = new SegmentTimeTiles[segments.Length];
        for (int index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentTimeTiles segment = SegmentTimeTiles.Of(segments[index], cancellationToken);
            tiles[index] = segment;
            rows += segment.Rows;
            withoutTime += segment.Untimed;
            if (segment.First is { } first && segment.Last is { } last)
            {
                minimum = Math.Min(minimum, first);
                maximum = Math.Max(maximum, last);
            }
        }

        if (minimum == long.MaxValue)
        {
            return (null, [], [], null, rows, withoutTime);
        }

        var extent = new TimeRange(minimum, maximum + 1);
        var main = new TimelineColumns(extent, MaximumTimelineBuckets, tallyMechanisms: true);
        (TimeRange span, int count) = SessionMinimap.ColumnsFor(extent);
        var minimap = new TimelineColumns(span, count, tallyMechanisms: false);
        for (int index = 0; index < segments.Length; index++)
        {
            tiles[index].CountInto(segments[index], main, cancellationToken);
            tiles[index].CountInto(segments[index], minimap, cancellationToken);
        }

        var overviewMinimap = new SessionMinimap(
            extent,
            Array.AsReadOnly([.. minimap.Counts]),
            Array.AsReadOnly(minimap.CaptureCoverage(coverage, clock, within: extent)))
        {
            ColumnSpan = span,
        };
        return (extent, main.Buckets(coverage, clock), main.MechanismLanes(coverage, clock), overviewMinimap, rows, withoutTime);
    }

    internal static bool Admitted(RelationStrength strength, EvidencePolicy policy) => strength switch
    {
        RelationStrength.Direct => true,
        RelationStrength.Correlated => policy >= EvidencePolicy.IncludeCorrelated,
        RelationStrength.Candidate => policy >= EvidencePolicy.IncludeCandidates,
        RelationStrength.Conflicting => policy >= EvidencePolicy.AllIncludingConflicting,
        _ => false,
    };

    /// <summary>The graph edge a paired TCP relation contributes to: one edge per unordered pair of process instances.</summary>
    internal static string EdgeKeyOf(TransportRelation relation) =>
        $"tcp:{Pair(relation.First.Id, relation.Second.Id).First}:{Pair(relation.First.Id, relation.Second.Id).Second}";

    internal static Channel ProjectChannel(TransportRelation relation) => new(
        relation.StableKey,
        EdgeKeyOf(relation),
        $"{relation.FirstEndpoint} ↔ {relation.SecondEndpoint}",
        Mechanism.Tcp,
        Direction.UnknownDirection,
        relation.Records,
        null,
        CoverageState.UnknownCoverage);

    private static (ProcessInstanceId First, ProcessInstanceId Second) Pair(
        ProcessInstanceId first, ProcessInstanceId second) =>
        string.CompareOrdinal(first.ToString(), second.ToString()) <= 0 ? (first, second) : (second, first);

    private static string GroupKey(ProcessInstance instance) =>
        string.IsNullOrWhiteSpace(instance.ImagePath) ? "executable:unknown" : "executable:" + instance.ImagePath.ToUpperInvariant();

    /// <summary>The witnessed image path a group's short name stands for; null when none was witnessed.</summary>
    private static string? GroupPath(ProcessInstance instance) =>
        string.IsNullOrWhiteSpace(instance.ImagePath) ? null : instance.ImagePath;
}
