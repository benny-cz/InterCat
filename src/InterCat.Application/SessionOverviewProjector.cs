using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One immutable, evidence-bounded overview. Its edges are only paired TCP relationships whose two process instances
/// are admitted under the requested policy; they are not a total of all traffic. The timeline counts all observations
/// with a usable session time and projects capture coverage only when this generation publishes a ledger. The graph's
/// own counts are the displayed relations' records, a narrower scope that never stands in for the timeline's. Each node
/// carries its own records (`process-activity-v1`), which rank the groups and processes of the ladder's table.
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
    /// An interval package's coverage within its interval, where its ledger speaks (`redacted-session-v1` §11), whose
    /// <see cref="MechanismCoverage"/> over its whole time is unknown; null for any other session.
    /// </summary>
    public IReadOnlyList<MechanismCoverage>? IntervalCoverage { get; init; }

    /// <summary>
    /// The clock this generation's readings are on, which places anything else read on it - such as a live preview's
    /// bins - on the same presentation axis as the timeline.
    /// </summary>
    public SourceClockDescriptor? Clock { get; init; }

    /// <summary>
    /// Rows no process instance holds: they name no owner, name a PID at a reading no instance of it held, or bind only
    /// as strongly as the evidence policy withholds. The timeline counts them; the ranked table's processes cannot.
    /// </summary>
    public long RowsNoProcessHolds { get; init; }

    /// <summary>The interval the capture recorded (<see cref="SessionRecording"/>); null when it recorded no stop.</summary>
    public TimeRange? Recording { get; init; }

    /// <summary>
    /// The process instances the capture's collectors are, InterCat's own (`collector-binding-v1`), and the collectors no
    /// instance is; none recorded for a capture that names none.
    /// </summary>
    public CollectorMatch Collectors { get; init; } = CollectorMatch.NotRecorded;

    /// <summary>
    /// When the capture began by the wall clock it recorded: session time 0, in UTC (<see cref="SessionRecording.Began"/>);
    /// null when it recorded none.
    /// </summary>
    public DateTimeOffset? Began { get; init; }

    /// <summary>
    /// The wall clock the capture's machine read, placed against session time (<see cref="SessionRecording.WallClock"/>);
    /// null when it recorded none.
    /// </summary>
    public SessionWallClock? WallClock { get; init; }

    /// <summary>The digest of the one generation's manifest the overview was built from (I16).</summary>
    public string? ManifestDigest { get; init; }

    /// <summary>
    /// The generation's size on disk, as its manifest measured every file it names, and the records it holds (§12.1 S5);
    /// null for an overview no generation was projected for.
    /// </summary>
    public SessionSize? Size { get; init; }

    /// <summary>Whether the session is InterCat's generated demo (<see cref="DemoInvestigation"/>), which every view says.</summary>
    public bool Demo { get; init; }

    /// <summary>
    /// How strongly a record had to bind to a process to count as that process's, and a relationship to be drawn, in this
    /// overview: correlated evidence unless a person chose otherwise.
    /// </summary>
    public EvidencePolicy Policy { get; init; } = EvidencePolicy.IncludeCorrelated;
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
        CoverageLedgerV1? coverage = SessionSegments.CoverageLedger(store.Root, manifest);
        SessionRedaction? redaction = SessionRedaction.Read(store.Root, manifest);
        SessionDerivation derivation = SessionDerivationCache.For(manifest);

        // A finished session names a derivation checkpoint and a persisted overview covering every segment, and then
        // its first view opens no segment (§12.1 S1, S4). A segment is opened only for what neither holds.
        SegmentReaderV1[]? opened = null;
        SegmentReaderV1[]? openedFields = null;
        (ProcessInstanceIndex processes, TransportRelationIndex relations, ProcessActivityIndex? saved) =
            derivation.FromCheckpoint(store.Root, clock)
            ?? (derivation.Processes(store.Root, Segments(), clock, Fields(), cancellationToken),
                derivation.Relations(store.Root, Segments(), clock, Fields(), cancellationToken),
                null);
        ProcessActivityIndex activity = saved ?? derivation.Activity(store.Root, Segments(), clock, Fields(), cancellationToken);
        OverviewCounts counted = derivation.PersistedOverview(store.Root) ?? Count(Segments(), cancellationToken);

        // Every instance and relationship is in the bundle, however many there are. What the graph draws at once is the
        // display projection's bound (GraphProjection, §6.3): it clusters rather than omitting anyone.
        int[] positions = [.. Enumerable.Range(0, processes.Instances.Count)
            .OrderBy(position => processes.Instances[position].Id.ToString(), StringComparer.Ordinal)];
        ProcessInstance[] ordered = [.. positions.Select(position => processes.Instances[position])];
        IGrouping<string, ProcessInstance>[] executables = [.. ordered
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)];
        Dictionary<string, string> names = ExecutableNames.Label(
            [.. executables.Select(group => (group.Key, group.First().ImagePath))]);
        ProcessGroup[] groups = [.. executables.Select(group => new ProcessGroup(
            group.Key, names[group.Key], LaneGrouping.Executable, GroupPath(group.First())))];

        // A row counts over the whole session, so it is as complete as the capture was over every epoch (§10.3): a
        // process's by everything the capture collected, since it could have made any of it, and a channel's by TCP.
        CoverageState captured = SessionCoverage.Capture(coverage);

        // The instances that collected the capture, by the PID and creation time it names them by (collector-binding-v1).
        CollectorMatch collectors = CollectorBinding.Match(processes.Instances, CollectorIdentitiesV1.Read(store.Root, manifest));
        CoverageState tcpCoverage = SessionCoverage.Of(coverage, Mechanism.Tcp).State;
        ProcessNode[] nodes = [.. ordered.Select((instance, index) => new ProcessNode(
            instance.Id,
            instance.ProcessId,
            instance.ImageName ?? ProcessNode.PidName(instance.ProcessId),
            RoleOf(instance.Witness),
            GroupKey(instance),
            0.5 + 0.38 * Math.Cos(2 * Math.PI * index / Math.Max(1, ordered.Length)),
            0.5 + 0.38 * Math.Sin(2 * Math.PI * index / Math.Max(1, ordered.Length)),
            captured)
        {
            Activity = ActivityOf(activity, positions[index], policy),
            PidHolder = (int)instance.LifecycleEpoch,
            PidHolders = processes.InstancesOf(instance.ProcessId),
            WithheldRecords = activity.WithheldOf(positions[index], policy),
            CandidateRecords = activity.CandidatesOf(positions[index]),
            ParentProcessId = instance.ParentProcessId,
            TerminalSession = instance.SessionId,
            Parent = instance.Parent,
            ParentBinding = instance.ParentBinding,
            Collector = collectors.RoleOf(instance.Id),
        })];

        // Every row is held by at most one instance, so what no instance holds is the rest: rows naming no owner, naming
        // a PID at a reading none of its instances held, or bound only as strongly as the policy withholds.
        long heldByProcesses = nodes.Sum(node => node.Records);
        long heldByNone = counted.Rows - heldByProcesses;

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
                group.Max(relation => relation.Strength))
            {
                // Each connection by the key its channel rung opens: the first record of the connection at either end.
                Rule = RelationRule.TransportEndpoint,
                Evidence = [.. group.Select(relation => relation.StableKey).Order(StringComparer.Ordinal)],
            })];

        // The processes a served RPC call joins, when the capture collected ALPC to follow calls through (operations-v1
        // §5c): from the links a persisted overview kept, or followed here when it kept none. A session without ALPC
        // reads nothing for them, so its first view opens no segment on their account.
        RpcPeerEdge[] rpc = !RpcPeerEdges.Collected(coverage) ? []
            : counted.RpcLinks is { } kept ? [.. RpcPeerEdges.Of(kept, policy)]
            : [.. RpcPeerEdges.Of(derivation.RpcPeers(store.Root, Segments(), clock, Fields(), cancellationToken), policy)];
        edges = [.. edges, .. rpc.Select(edge => new CommunicationEdge(
            edge.Key, edge.First, edge.Second, Mechanism.Rpc, edge.Records, null, edge.Strength)
        {
            // Its own key opens the calls its links join, at both ends: the links are kept as counts, never as a list.
            Rule = RelationRule.RpcCallPeer,
            Evidence = [edge.Key],
        })];

        string? channelProblem = admitted.Length > maximumChannels
            ? $"This generation has {admitted.Length:N0} admitted paired TCP channels, above the "
                + $"{maximumChannels:N0}-channel overview bound. The graph and timeline remain available; "
                + "the channel rung needs a scoped query before it can show the complete set. "
                + "Use icat channels <session-directory> --process <instance-guid> to page admitted channels."
            : null;
        Channel[] channels = channelProblem is not null ? [] : [.. admitted
            .OrderBy(relation => relation.StableKey, StringComparer.Ordinal)
            .Select(relation => ProjectChannel(relation, tcpCoverage))];
        if (channelProblem is null
            && channels.Select(channel => channel.Key).Distinct(StringComparer.Ordinal).Count() != channels.Length)
        {
            throw new InvalidDataException(
                "Two admitted TCP incarnations have the same raw-fact channel key. Refusing to merge them.");
        }

        int unrelated = nodes.Length - edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Distinct().Count();

        // What the graph draws is counted from the relations themselves: a relation holds exactly the records at its two
        // ends, and every record of an incarnation has that incarnation's other end, so no row is looked up again here.
        long graphRows = admitted.Sum(relation => relation.Records) + rpc.Sum(edge => edge.Records);
        long graphWithoutTime = admitted.Sum(relation => relation.RecordsWithoutSessionTime);
        long unresolved = relations.RecordsWithoutAdmittedPeer(Mechanism.Tcp, policy);
        (TimeRange? extent, TimelineBucket[] timeline, MechanismTimelineLane[] lanes, SessionMinimap? minimap,
            long rows, long withoutTime) = Timeline(counted, clock, coverage);
        string[] caveats =
        [
            "Graph edges show paired TCP connection incarnations whose two process instances are admitted. "
                + "One-sided, ambiguous and unsupported relationships are not rendered as guessed edges.",
            .. (rpc.Length == 0 ? [] : new[]
            {
                "RPC edges join a process and the process that served its calls, each call linked through its one ALPC "
                    + "message (rpc-call-peer-v1). An edge counts the call records at its two ends; the ALPC records that "
                    + "link them are its evidence, not a second count.",
            }),
            "Edge direction is a stable display order, not a claim about which process initiated or sent data. "
                + "Edge record counts include observations from both ends and are not whole-session totals.",
            coverage is null
                ? "The timeline counts observed rows only. This legacy generation publishes no coverage ledger, "
                    + "so its coverage is unknown; an empty interval is not proof of inactivity."
                : "Timeline coverage describes the captured mechanisms of observed rows within the ledger's "
                    + "delivered readings. An empty bucket takes the capture's own coverage there: quiet where a live "
                    + "capture's sources covered it, unknown where the ledger cannot say, as an import's cannot. Source "
                    + "loss has no finer location than its epoch, and coverage does not prove a graph relationship complete.",
            $"{withoutTime:N0} {(withoutTime == 1 ? "row has" : "rows have")} no usable session time and "
                + $"{(withoutTime == 1 ? "is" : "are")} absent from the timeline; "
                + $"{CountText.Of(unresolved, "TCP row")} {CountText.Agree(unresolved, "has", "have")} no admitted peer; "
                + $"{CountText.Of(notAdmitted, "paired relationship")} {CountText.Agree(notAdmitted, "was", "were")} withheld by the "
                + "evidence policy.",
            // An edge holds records at both of its ends, so the graph's rows are never one.
            string.Create(CultureInfo.CurrentCulture, $"{graphRows:N0} rows belong to displayed graph edges; {graphWithoutTime:N0} of them ")
                + $"{CountText.Agree(graphWithoutTime, "has", "have")} no usable session time and "
                + $"{CountText.Agree(graphWithoutTime, "is", "are")} among the rows absent from the timeline.",
            $"The ranked table orders groups and processes by their own records ({ProcessActivityIndex.CountRule}): "
                + $"{CountText.Of(heldByProcesses, "row")} {CountText.Agree(heldByProcesses, "binds", "bind")} to a process "
                + $"instance the evidence policy admits. The other {heldByNone:N0} "
                + $"{CountText.Agree(heldByNone, "names", "name")} no owner ({activity.RecordsWithoutOwner:N0}), "
                + $"{CountText.Agree(heldByNone, "names", "name")} a PID at a reading no instance of it held "
                + $"({activity.RecordsNotBound:N0}), or {CountText.Agree(heldByNone, "binds", "bind")} only as strongly as the "
                + $"policy withholds; the timeline counts {CountText.Agree(heldByNone, "it", "them")} and no process does.",
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
            .. (redaction is null ? [] : new[] { redaction.Statement(CultureInfo.CurrentCulture) }),
            .. (derivation.CheckpointProblem is not { } problem
                ? []
                : new[]
                {
                    $"This generation's derivation checkpoint was not used. {problem} Processes and relationships were "
                        + "derived from every segment instead, which takes longer and gives the same result.",
                }),
            .. (derivation.OverviewProblem is not { } overviewProblem
                ? []
                : new[]
                {
                    $"This generation's persisted overview was not used. {overviewProblem} The timeline and minimap were "
                        + "counted from every segment instead, which takes longer and gives the same result.",
                }),
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
            RowsNoProcessHolds = heldByNone,
            Recording = SessionRecording.Interval(store.Root, manifest, clock, extent),
            Began = SessionRecording.Began(store.Root, manifest, clock),
            WallClock = SessionRecording.WallClock(store.Root, manifest, clock),
            ManifestDigest = manifest.Digest,
            Size = SessionGrowth.Measure(manifest, rows),
            Demo = DemoInvestigation.IsDemo(manifest),
            Policy = policy,
            Collectors = collectors,
            IntervalCoverage = coverage is { HoldsRecordsOutsideItsEpochs: true }
                ? Array.AsReadOnly([.. SessionCoverage.ByMechanism(coverage with { HoldsRecordsOutsideItsEpochs = false })])
                : null,
        };

        SegmentReaderV1[] Segments() => opened ??=
            [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];

        SegmentReaderV1[] Fields() => openedFields ??=
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
    }

    /// <summary>
    /// What the overview counts from a generation's rows: how many there are, how many have no session time, the extent
    /// of the rest, and their counts in the overview's columns and the minimap's. It is what a persisted overview holds
    /// (`contracts/overview-index-v1.md`); coverage is judged when the counts are presented.
    /// </summary>
    internal static OverviewCounts Count(IReadOnlyList<SegmentReaderV1> segments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // Every count comes from the segments' tiles (§12.1 S4's first level), built once per reader: the extent from
        // their earliest and latest readings, and the buckets and minimap columns from tiles whole wherever a tile's
        // records fall in one column. Only a tile a bucket boundary crosses has its rows read (§10.3).
        long rows = 0;
        long withoutTime = 0;
        long minimum = long.MaxValue;
        long maximum = long.MinValue;
        var tiles = new SegmentTimeTiles[segments.Count];
        for (int index = 0; index < segments.Count; index++)
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
            return new(rows, withoutTime, null, null, null);
        }

        var extent = new TimeRange(minimum, maximum + 1);
        var main = new TimelineColumns(extent, MaximumTimelineBuckets, tallyMechanisms: true);
        (TimeRange span, int count) = SessionMinimap.ColumnsFor(extent);
        var minimap = new TimelineColumns(span, count, tallyMechanisms: false);
        for (int index = 0; index < segments.Count; index++)
        {
            tiles[index].CountInto(segments[index], main, cancellationToken);
            tiles[index].CountInto(segments[index], minimap, cancellationToken);
        }

        return new(rows, withoutTime, extent, main, minimap);
    }

    /// <summary>The timeline, its mechanism lanes and the minimap the counts give, with coverage judged by the ledger.</summary>
    private static (TimeRange? Extent, TimelineBucket[] Buckets, MechanismTimelineLane[] Lanes, SessionMinimap? Minimap,
        long Rows, long WithoutTime)
        Timeline(OverviewCounts counts, SourceClockDescriptor clock, CoverageLedgerV1? coverage)
    {
        if (counts is not { Extent: { } extent, Main: { } main, Minimap: { } minimap })
        {
            return (null, [], [], null, counts.Rows, counts.WithoutTime);
        }

        var overviewMinimap = new SessionMinimap(
            extent,
            Array.AsReadOnly([.. minimap.Counts]),
            Array.AsReadOnly(minimap.CaptureCoverage(coverage, clock, within: extent)))
        {
            ColumnSpan = minimap.Interval,
        };
        // A quiet interval of the whole capture is judged by what it collected there, as the minimap's is (R21).
        return (extent, main.Buckets(coverage, clock, main.CaptureCoverage(coverage, clock, within: extent)),
            main.MechanismLanes(coverage, clock), overviewMinimap, counts.Rows, counts.WithoutTime);
    }

    /// <summary>An instance's own records the policy admits, by mechanism, most first (`process-activity-v1`).</summary>
    internal static IReadOnlyList<MechanismCount> ActivityOf(ProcessActivityIndex activity, int position, EvidencePolicy policy) =>
        Array.AsReadOnly([.. activity.MechanismsOf(position, policy).Select(entry => new MechanismCount(entry.Mechanism, entry.Records))]);

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

    /// <summary>A paired TCP relation as a channel, with TCP's coverage over the scope its count is over.</summary>
    internal static Channel ProjectChannel(TransportRelation relation, CoverageState coverage) => new(
        relation.StableKey,
        EdgeKeyOf(relation),
        ChannelNames.Of(relation.FirstEndpoint, relation.SecondEndpoint),
        Mechanism.Tcp,
        Direction.UnknownDirection,
        relation.Records,
        null,
        coverage)
    {
        FirstHolder = relation.First.Id,
        SecondHolder = relation.Second.Id,
        Rule = RelationRule.TransportEndpoint,
        Strength = relation.Strength,
        OpenWitnessed = relation.OpenWitnessed,
        CloseWitnessed = relation.CloseWitnessed,
    };

    /// <summary>
    /// What an instance's existence rests on, in the words `icat processes` uses (R5): the enumeration's own name read
    /// "ActivityOnly" wherever a process was described.
    /// </summary>
    internal static string RoleOf(ProcessWitness witness) => witness switch
    {
        ProcessWitness.Created => "created during capture",
        ProcessWitness.Rundown => "running at capture start",
        ProcessWitness.ExitOnly => "running before its first record",
        ProcessWitness.ActivityOnly => "seen only in its own records",
        _ => witness.ToString(),
    };

    private static (ProcessInstanceId First, ProcessInstanceId Second) Pair(
        ProcessInstanceId first, ProcessInstanceId second) =>
        string.CompareOrdinal(first.ToString(), second.ToString()) <= 0 ? (first, second) : (second, first);

    internal static string GroupKey(ProcessInstance instance) =>
        string.IsNullOrWhiteSpace(instance.ImagePath)
            ? ProcessGroup.UnwitnessedExecutableKey
            : "executable:" + instance.ImagePath.ToUpperInvariant();

    /// <summary>The witnessed image path a group's short name stands for; null when none was witnessed.</summary>
    private static string? GroupPath(ProcessInstance instance) =>
        string.IsNullOrWhiteSpace(instance.ImagePath) ? null : instance.ImagePath;
}
