using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;


/// <summary>One labelled fact about the selected evidence record, as the inspector lists it.</summary>
public sealed record EvidenceField(string Label, string Value) : IAccessibleRow
{
    public string AccessibleName => $"{Label}: {Value}";
}

/// <summary>
/// What a hover over a drawn mark states - a graph node or edge (§6.3) or a timeline bucket - under §6.2's hover
/// contract: a title and one fact per line.
/// </summary>
public sealed record HoverCard(string Title, IReadOnlyList<string> Lines);

/// <summary>A keyboard-addressable counterpart of an L0 mechanism row; null means the whole timeline.</summary>
public sealed record TimelineLaneOption(Mechanism? Mechanism, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Mechanism is null
        ? "All mechanisms; show whole-session interval counts"
        : $"{Label} mechanism lane; show its exact interval counts and step within this lane";
}

/// <summary>A keyboard-addressable L2 source-direction row; null shows the whole owner focus.</summary>
public sealed record DirectionLaneOption(Direction? Direction, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Direction is null
        ? "All directions; show this process's complete interval counts"
        : $"{Label} source-direction lane; show its exact interval counts and step within this lane";
}

/// <summary>A keyboard-addressable L3 channel end; a null End shows the whole channel's counts.</summary>
public sealed record ChannelEndOption(int? End, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => End is null
        ? "Both ends; show the channel's complete interval counts"
        : $"{Label} end; show its exact interval counts and step within this end";
}

/// <summary>UI intent that can be rebased onto a later published generation of the same session.</summary>
/// <param name="SelectedGraphAggregate">A selected aggregate node that is not one executable group, by its stable key.</param>
/// <param name="SelectedChannelEnd">The L3 end chosen for the table and stepping: 0 the channel's first end, 1 its second.</param>
/// <param name="Forward">The rungs forward steps would re-enter, nearest first (§6.7).</param>
/// <param name="RankBy">What the machine and group rungs rank by (§6.1's metric selector).</param>
public sealed record WorkspaceNavigationMemento(
    IReadOnlyList<NavigationState> Breadcrumb,
    ProcessInstanceId? SelectedProcess,
    TimeRange? SelectedInterval,
    string? SelectedRungKey,
    bool ShowTables,
    string? SelectedGraphAggregate = null,
    string SearchText = "",
    string? SelectedSearchKey = null,
    Mechanism? SelectedTimelineMechanism = null,
    Direction? SelectedTimelineDirection = null,
    int? SelectedChannelEnd = null,
    IReadOnlyList<NavigationState>? Forward = null,
    RankingMetric RankBy = RankingMetric.Records,
    bool PerSecond = false,
    bool ScalesEachLane = false);

/// <summary>
/// What one publication's ranking counted: the visible range it followed and the interval counts it showed, with the
/// interval they answer, and the bytes and RPC calls a ranking read for the whole session and for that interval. The
/// next publication of the same session shows them, marked pending, until its own arrive.
/// </summary>
public sealed record ScopeCarry(
    TimeRange? VisibleRange,
    TimeRange? Interval,
    SessionIntervalCounts? Counts,
    SessionByteMeasures? WholeBytes = null,
    SessionByteMeasures? IntervalBytes = null,
    SessionCallMeasures? WholeCalls = null,
    SessionCallMeasures? IntervalCalls = null,
    SessionPeerMeasures? WholePeers = null,
    SessionPeerMeasures? IntervalPeers = null);

/// <summary>
/// What one publication's timeline drew beyond the overview: its zoomed detail, the counts of the focus it was drawn for,
/// by that focus's key, and the bytes its lanes plotted under a byte ranking, a group's or a process's for that focus. The
/// next publication of the same session shows them until its own arrive.
/// </summary>
public sealed record TimelineCarry(
    SessionTimelineDetail? Detail,
    string? FocusKey,
    IReadOnlyList<TimelineBucket>? Focus,
    IReadOnlyList<ProcessTimelineLane>? ProcessLanes = null,
    string? ProcessLaneProblem = null,
    IReadOnlyList<DirectionTimelineLane>? DirectionLanes = null,
    IReadOnlyList<ChannelEndTimelineLane>? ChannelEndLanes = null,
    string? HighlightKey = null,
    IReadOnlyList<TimelineBucket>? Highlight = null,
    IReadOnlyList<MechanismTimelineLane>? HighlightLanes = null,
    SessionMechanismByteMeasures? LaneBytes = null,
    SessionMechanismByteMeasures? ZoomedLaneBytes = null,
    SessionOwnerByteMeasures? ProcessLaneBytes = null,
    SessionDirectionByteMeasures? DirectionLaneBytes = null);

public sealed partial class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GraphLayoutScheduler graphLayout;
    private readonly WorkspaceSelectionCoordinator selection = new();
    private readonly DetailLadder ladder;
    private ProcessNode? selectedProcess;
    private TimeRange? selectedInterval;
    private RungRow? selectedRung;
    private CrumbRow? selectedCrumb;
    private FilterRow? selectedFilter;
    private RelationshipRow? selectedRelationship;
    private bool restatingRelationships;
    private IntervalRow? selectedIntervalRow;
    private LadderView view;
    private bool showTables;
    private bool disposed;
    private readonly string workspaceDisclosure;
    private string? awaitingCaptureNote;
    private string? awaitingCaptureTitle;
    private readonly bool realOverview;
    private readonly bool emptyWorkspace;
    private readonly SessionEvidenceSource? evidenceSource;
    private EvidenceList? evidence;
    private SessionEvidenceRecord? selectedEvidence;
    private string? pendingEvidenceKey;
    private IReadOnlyList<RungRow> evidenceRows = [];

    // A published process's RPC channels at its rung, and one RPC channel's calls at its rung, read from the session: the
    // overview holds neither (`contracts/operations-v1.md` §5). Each belongs to the rung it was read for.
    private RpcChannelsLoad? rpcChannels;
    private RpcCallsLoad? rpcCalls;
    private IReadOnlyList<RungRow> rpcChannelRows = [];
    private IReadOnlyList<RungRow> rpcCallRows = [];

    // While the ladder climbs, the key the rung it left was focused on: a call's, when it left the call's records.
    private string? revealCall;

    // Whether the user chose a ranked row last that is neither a process nor a group, so the inspector describes it.
    private bool describesRow;

    // The RPC channel rung's calls within the drawn viewport, for the timeline's call lane, and the read under way.
    private RpcCallSpanPage? rpcSpans;
    private (string Key, TimeRange Viewport, int Columns)? requestedRpcSpans;
    private CancellationTokenSource? rpcSpanQuery;
    private readonly WorkspaceSnapshot wholeSnapshot;
    private WorkspaceSnapshot? scopedSnapshot;
    private readonly string graphIdentity;

    // The drawn graph: its structure from the whole session and the ladder's focus, its counts from the current scope.
    private GraphDisplay wholeDisplay;
    private GraphDisplay graphDisplay;
    private IReadOnlyDictionary<string, GraphPoint> displayPositions;
    private string? graphProjectionProblem;
    private string? graphWorkerProblem;

    /// <summary>
    /// An aggregate node the user selected that is not one executable group (no relationships, other members, other
    /// processes); null otherwise.
    /// </summary>
    private string? selectedClusterKey;

    /// <summary>An executable group selected by its ranked row or its collapsed node; null otherwise.</summary>
    private string? selectedGroupKey;

    /// <summary>Processes with at least one relationship in the published scope, which the graph draws rather than counts.</summary>
    private readonly HashSet<ProcessInstanceId> relatedProcesses;
    private TimeRange? scopedInterval;

    // The timeline's settled viewport while it shows less than the whole extent; the scope when nothing is brushed (§6.4).
    private TimeRange? visibleRange;

    // The interval the displayed counts actually answer; a brush still being counted is not yet applied.
    private TimeRange? appliedInterval;

    // The counts on screen and the interval they answer: this generation's own, or an earlier publication's standing in
    // while this one's are read. A stand-in is shown and carried on, but never claimed as applied or exported.
    private SessionIntervalCounts? displayedCounts;
    private TimeRange? displayedCountsInterval;
    private bool scopeStandsIn;
    private CancellationTokenSource? intervalQuery;
    private bool intervalLoading;
    private string? intervalProblem;
    private IReadOnlyList<RelationshipRow> relationships;
    private CancellationTokenSource? timelineQuery;

    // The count the timeline last asked for, and the viewport and columns the view last drew, which a new rung replays
    // with its own focus.
    private (TimeRange Viewport, int Columns, string? Focus)? requestedTimeline;
    private (TimeRange Viewport, int Columns)? drawnTimeline;
    private SessionTimelineDetail? timelineDetail;

    // The rung's timeline focus: what E would read from it, counted beside the whole timeline (§3.2).
    private TimelineFocus? timelineFocus;
    private string? timelineFocusDescription;
    private IReadOnlyList<TimelineBucket>? timelineFocusBuckets;
    private IReadOnlyList<ProcessTimelineLane>? timelineProcessLanes;
    private IReadOnlyList<DirectionTimelineLane>? timelineDirectionLanes;
    private IReadOnlyList<ChannelEndTimelineLane>? timelineChannelEnds;
    private LiveEdge? liveEdge;
    private IReadOnlyList<ChannelEndOption> channelEndOptions = [new(null, "Both ends")];
    private int? selectedChannelEnd;
    private IReadOnlyList<ProcessTimelineLane> processLaneDisplay = [];
    private string? processLaneProblem;
    private bool timelineFocusLoading;
    private string? timelineFocusProblem;

    // The selection's highlight in the timeline (§6.4): the selected entity's records, counted on the columns the timeline
    // draws and marked as a share of each bar. It is not a filter, and a selection that is the rung's own focus adds none.
    private TimelineFocus? highlight;
    private string? highlightName;

    // The one channel a chosen relationship rests on, whose records it highlights and E lists; any other selection, and
    // every navigation, lets it go. A row chosen among a process's rows is the described row (see ChosenRow).
    private string? chosenChannelKey;

    // §6.7's multi-selection: the processes Ctrl+click added, an explicit predicate. A plain selection or a navigation
    // lets it go; Enter turns it into a filter.
    private readonly HashSet<ProcessInstanceId> chosenProcesses = [];
    private CancellationTokenSource? highlightQuery;
    private (TimeRange Viewport, int Columns, string Focus)? requestedHighlight;
    private IReadOnlyList<TimelineBucket>? highlightBuckets;
    private IReadOnlyList<MechanismTimelineLane>? highlightLanes;
    private IReadOnlyList<IntervalRow> intervals;
    private readonly ReadOnlyCollection<TimelineLaneOption> timelineLaneOptions;
    private TimelineLaneOption selectedTimelineLane = new(null, "All mechanisms");
    private readonly ReadOnlyCollection<DirectionLaneOption> directionLaneOptions;
    private DirectionLaneOption selectedDirectionLane = new(null, "All directions");

    public WorkspaceViewModel() : this(SyntheticWorkspace.Create(), "synthetic-tour-v1")
    {
    }

    /// <summary>
    /// Presents one immutable workspace. The graph identity must name the same data revision as the snapshot,
    /// so an off-thread layout from an older revision can never replace its coordinates. <paramref name="carriedLayout"/>
    /// holds the drawn positions of an earlier publication of the same session: a node still drawn keeps its place, so a
    /// live refresh does not rearrange the graph under the user (§6.3).
    /// </summary>
    public WorkspaceViewModel(
        WorkspaceSnapshot snapshot,
        string graphIdentity,
        SessionEvidenceSource? evidenceSource = null,
        IReadOnlyDictionary<string, GraphPoint>? carriedLayout = null,
        IReadOnlyDictionary<string, GraphPoint>? carriedPins = null)
        : this(snapshot, graphIdentity, evidenceSource, carriedLayout, new GraphLayoutScheduler(), carriedPins)
    {
    }

    /// <summary>The same workspace with an injected layout scheduler, so a test can hold a layout while it reads a frame.</summary>
    internal WorkspaceViewModel(
        WorkspaceSnapshot snapshot,
        string graphIdentity,
        SessionEvidenceSource? evidenceSource,
        IReadOnlyDictionary<string, GraphPoint>? carriedLayout,
        GraphLayoutScheduler layoutScheduler,
        IReadOnlyDictionary<string, GraphPoint>? carriedPins = null)
    {
        graphLayout = layoutScheduler ?? throw new ArgumentNullException(nameof(layoutScheduler));
        wholeSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        realOverview = graphIdentity.StartsWith("session:", StringComparison.Ordinal);
        emptyWorkspace = graphIdentity == "empty-workspace";
        this.evidenceSource = realOverview ? evidenceSource : null;
        workspaceDisclosure = graphIdentity == "synthetic-tour-v1"
            ? "Synthetic interaction tour; none of these values are Windows capture evidence."
            : graphIdentity == "empty-workspace"
                ? "No live capture is running. Start exploring to see published evidence."
                : snapshot.Redaction is null
                    ? OverviewWorkspace.DisclosureFor(snapshot)
                    : OverviewWorkspace.RedactedDisclosure + " " + OverviewWorkspace.DisclosureFor(snapshot);
        // The drawn graph is projected from the whole session, so a brush re-counts it without re-clustering it (§6.4).
        this.graphIdentity = graphIdentity;
        relatedProcesses = [.. snapshot.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })];
        wholeDisplay = ProjectGraph(null, null);
        graphDisplay = wholeDisplay;

        // The first frame draws each node where the layout will start it: a carried position if the node was drawn before,
        // else its band's deterministic lattice (§19.4). The complete layout is computed off-thread and applied only if its
        // identity is still the graph shown. The snapshot's own coordinates are not positions anyone has seen.
        foreach ((string key, GraphPoint point) in carriedLayout ?? new Dictionary<string, GraphPoint>())
        {
            if (point.IsValid) layoutMemory[key] = point;
        }

        // A pin is where the user put a node; a later publication keeps it, and the first frame already shows it there.
        foreach ((string key, GraphPoint point) in carriedPins ?? new Dictionary<string, GraphPoint>())
        {
            if (!point.IsValid) continue;
            graphPins[key] = point;
            layoutMemory[key] = point;
        }

        Dictionary<string, GraphPoint> kept = Remembered(wholeDisplay);
        provisionalKeys.UnionWith(wholeDisplay.Nodes.Select(node => node.Key).Where(key => !kept.ContainsKey(key)));
        try
        {
            displayPositions = GraphLayout.SeedDisplay(graphIdentity, wholeDisplay, kept);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The graph fails closed and says why; the tables, ladder and timeline stay usable.
            displayPositions = new ReadOnlyDictionary<string, GraphPoint>(new Dictionary<string, GraphPoint>());
            graphWorkerProblem = exception.Message;
        }

        GraphPositions = ProcessPositions();
        ladder = new(SyntheticWorkspace.Root(Snapshot));
        view = ProjectLadder();
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeResources.CurrentMode);
        relationships = RelationshipRows();
        timelineLaneOptions = Array.AsReadOnly([new TimelineLaneOption(null, "All mechanisms"),
            .. wholeSnapshot.MechanismLanes.Select(lane =>
                new TimelineLaneOption(lane.Mechanism, EvidenceRowText.MechanismName(lane.Mechanism)))]);
        selectedTimelineLane = timelineLaneOptions[0];
        directionLaneOptions = Array.AsReadOnly([
            new DirectionLaneOption(null, "All directions"),
            .. SessionTimelineQuery.LaneDirections.Select(direction =>
                new DirectionLaneOption(direction, DirectionLabel(direction))),
        ]);
        selectedDirectionLane = directionLaneOptions[0];
        intervals = WorkspaceRowBuilder.Intervals(Snapshot, ThemeResources.CurrentMode, IntervalTableShowsBytes);
        if (ReadsBytes)
        {
            // Until the table is shown and reads them, a real session's rows say their bytes are not read yet.
            RefreshIntervalRows();
        }

        selection.SelectionChanged += OnSelectionChanged;
        selectedProcess = !realOverview && Snapshot.Processes.Count > 0 ? Snapshot.Processes[0] : null;
        if (selectedProcess is not null)
        {
            selection.SelectProcess(selectedProcess.Id);
        }

        LayoutReady = BuildLayoutAsync(graphIdentity, wholeDisplay);
    }

    /// <summary>
    /// The drawn graph for a focus. A workspace whose relationships name a process it does not hold cannot be drawn; the
    /// graph is then empty and says why, while the tables, ladder and timeline keep working.
    /// </summary>
    private GraphDisplay ProjectGraph(string? expanded, ProcessInstanceId? kept, IReadOnlySet<ProcessInstanceId>? neighborhood = null)
    {
        try
        {
            GraphDisplay projected = GraphProjection.Project(wholeSnapshot, expanded, kept, neighborhood: neighborhood);
            SetGraphProjectionProblem(null);
            return projected;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            SetGraphProjectionProblem(exception.Message);
            return new GraphDisplay([], [], wholeSnapshot.Processes.Count, expanded, kept);
        }
    }

    /// <summary>Re-counts an existing drawing without allowing a malformed scoped snapshot to take down the workspace.</summary>
    private GraphDisplay ScopeGraph(GraphDisplay display, WorkspaceSnapshot scoped)
    {
        if (display.Nodes.Count == 0 && display.TotalProcesses > 0 && graphProjectionProblem is not null)
        {
            return new GraphDisplay([], [], scoped.Processes.Count, display.ExpandedGroup, display.Kept);
        }

        try
        {
            GraphDisplay recounted = GraphProjection.Rescope(display, wholeSnapshot, scoped);
            SetGraphProjectionProblem(null);
            return recounted;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            SetGraphProjectionProblem(exception.Message);
            return new GraphDisplay([], [], scoped.Processes.Count, display.ExpandedGroup, display.Kept);
        }
    }

    private void SetGraphProjectionProblem(string? problem)
    {
        string? before = GraphLayoutProblem;
        graphProjectionProblem = problem;
        NotifyGraphProblemChanged(before);
    }

    private void SetGraphWorkerProblem(string? problem)
    {
        string? before = GraphLayoutProblem;
        graphWorkerProblem = problem;
        NotifyGraphProblemChanged(before);
    }

    private void NotifyGraphProblemChanged(string? before)
    {
        if (string.Equals(before, GraphLayoutProblem, StringComparison.Ordinal))
        {
            return;
        }

        OnPropertyChanged(nameof(GraphLayoutProblem));
        OnPropertyChanged(nameof(GraphSummary));
    }

    /// <summary>The last laid-out position of each node of <paramref name="display"/> that has one.</summary>
    private Dictionary<string, GraphPoint> Remembered(GraphDisplay display)
    {
        var remembered = new Dictionary<string, GraphPoint>(StringComparer.Ordinal);
        foreach (GraphDisplayNode node in display.Nodes)
        {
            if (layoutMemory.TryGetValue(node.Key, out GraphPoint point)) remembered[node.Key] = point;
        }

        return remembered;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// What the ranking, graph and tables show: the whole session, or the same entities counted only inside the brushed
    /// analysis interval once that count has arrived (plan §6.4). Identities, positions and the timeline never change.
    /// </summary>
    public WorkspaceSnapshot Snapshot => scopedSnapshot ?? wholeSnapshot;

    /// <summary>The published generation as projected, before any analysis interval scoped its counts.</summary>
    public WorkspaceSnapshot WholeSnapshot => wholeSnapshot;

    /// <summary>Completes when the most recent analysis-interval ranking has applied, been cleared or reported a problem.</summary>
    public Task IntervalReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The timeline at the viewport's own resolution once its count has arrived; null at the whole extent, which the
    /// overview's buckets already answer, and until a zoomed count completes (plan §6.2).
    /// </summary>
    public SessionTimelineDetail? TimelineDetail => timelineDetail;

    /// <summary>The generation this workspace was projected from, when it shows a real session.</summary>
    public long? DisplayedGeneration => evidenceSource?.Generation;

    /// <summary>Completes when the most recent timeline-detail request has applied, been superseded or failed.</summary>
    public Task TimelineDetailReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Asks for the timeline over a viewport in <paramref name="columns"/> columns. The overview's coarse buckets stay
    /// on screen until the answer arrives, a newer viewport cancels an older request, and the whole extent needs none
    /// (P25). A request that fails leaves the coarse buckets, which remain true at their own resolution.
    /// </summary>
    public void RequestTimelineDetail(TimeRange viewport, int columns)
    {
        columns = Math.Clamp(columns, 1, SessionTimelineQuery.MaximumColumns);
        if (disposed)
        {
            return;
        }

        drawnTimeline = (viewport, columns);
        RequestHighlight();
        RequestRpcSpans(viewport);
        RequestHttpSpans(viewport);
        FollowTimelineBytes();
        TimeRange extent = wholeSnapshot.Extent;
        bool whole = viewport.StartTicks <= extent.StartTicks && viewport.EndTicks >= extent.EndTicks;
        if (whole && timelineFocus is not null && wholeSnapshot.Timeline.Count > 0)
        {
            // At the whole extent the focus is counted on the overview's own columns, so each focus bar stands inside
            // the bar drawn for the same interval.
            viewport = extent;
            columns = wholeSnapshot.Timeline.Count;
        }

        (TimeRange, int, string?) request = (viewport, columns, timelineFocus?.Key);
        if (requestedTimeline == request)
        {
            return;
        }

        requestedTimeline = request;
        timelineQuery?.Cancel();
        timelineQuery?.Dispose();
        timelineQuery = null;
        if (evidenceSource is null || (whole && timelineFocus is null))
        {
            SetTimelineDetail(null, null);
            TimelineDetailReady = Task.CompletedTask;
            return;
        }

        var query = new CancellationTokenSource();
        timelineQuery = query;
        TimelineDetailReady = LoadTimelineDetailAsync(evidenceSource, viewport, columns, whole, timelineFocus, query);
    }

    private async Task LoadTimelineDetailAsync(
        SessionEvidenceSource source,
        TimeRange viewport,
        int columns,
        bool whole,
        TimelineFocus? focus,
        CancellationTokenSource query)
    {
        if (focus is not null)
        {
            SetTimelineFocusState(loading: true, problem: null);
        }

        try
        {
            if (focus is null)
            {
                SessionTimelineDetail detail = await source.TimelineAsync(viewport, columns, query.Token)
                    .AnsweredLater();
                if (!disposed && ReferenceEquals(timelineQuery, query))
                {
                    SetTimelineDetail(detail.SessionId == source.SessionId ? detail : null, null);
                }

                return;
            }

            SessionFocusedTimeline counted = await source.FocusedTimelineAsync(viewport, columns, focus, query.Token)
                .AnsweredLater();
            if (!disposed && ReferenceEquals(timelineQuery, query))
            {
                bool sameSession = counted.Whole.SessionId == source.SessionId;

                // At the whole extent the overview's buckets are the whole timeline; the focus was counted on their columns.
                SetTimelineDetail(sameSession && !whole ? counted.Whole : null, sameSession ? counted.Focus : null,
                    sameSession ? counted.ProcessLanes : null, sameSession ? counted.ProcessLaneProblem : null,
                    sameSession ? counted.DirectionLanes : null, sameSession ? counted.ChannelEndLanes : null);
                SetTimelineFocusState(loading: false, sameSession ? null : "the session on disk is another one");
            }
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            // A newer viewport, a new rung or a closed workspace superseded this count.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && ReferenceEquals(timelineQuery, query))
            {
                SetTimelineDetail(null, null);
                if (focus is not null)
                {
                    SetTimelineFocusState(loading: false, exception.Message);
                }
            }
        }
    }

    private void SetTimelineDetail(SessionTimelineDetail? detail, IReadOnlyList<TimelineBucket>? focus,
        IReadOnlyList<ProcessTimelineLane>? processLanes = null, string? laneProblem = null,
        IReadOnlyList<DirectionTimelineLane>? directionLanes = null,
        IReadOnlyList<ChannelEndTimelineLane>? channelEnds = null)
    {
        if (ReferenceEquals(timelineDetail, detail) && ReferenceEquals(timelineFocusBuckets, focus)
            && ReferenceEquals(timelineProcessLanes, processLanes) && processLaneProblem == laneProblem
            && ReferenceEquals(timelineDirectionLanes, directionLanes)
            && ReferenceEquals(timelineChannelEnds, channelEnds))
        {
            return;
        }

        timelineDetail = detail;
        timelineFocusBuckets = focus;
        timelineProcessLanes = processLanes;
        timelineDirectionLanes = directionLanes;
        if (!ReferenceEquals(timelineChannelEnds, channelEnds))
        {
            // The selector offers the ends this count names; an end chosen earlier keeps its place by its number.
            timelineChannelEnds = channelEnds;
            channelEndOptions = [new(null, "Both ends"), .. (channelEnds ?? []).Select(end => new ChannelEndOption(end.End, ChannelEndLabel(end)))];
            OnPropertyChanged(nameof(ChannelEndOptions));
            OnPropertyChanged(nameof(SelectedChannelEndOption));
        }

        processLaneDisplay = processLanes is null ? [] : OrderProcessLanes(processLanes);
        processLaneProblem = laneProblem;
        RefreshIntervalRows(focus);

        OnPropertyChanged(nameof(TimelineDetail));
        OnPropertyChanged(nameof(TimelineFocusBuckets));
        OnPropertyChanged(nameof(TimelineProcessLanes));
        OnPropertyChanged(nameof(TimelineDirectionLanes));
        OnPropertyChanged(nameof(ShowsDirectionLanes));
        OnPropertyChanged(nameof(TimelineChannelEndLanes));
        OnPropertyChanged(nameof(ShowsChannelEndLanes));
        OnPropertyChanged(nameof(ProcessLaneDisplay));
        OnPropertyChanged(nameof(ShowsProcessLanes));
        OnPropertyChanged(nameof(HasSelectedProcessLane));
        OnPropertyChanged(nameof(ProcessLaneProblem));

        // A group's lanes counted anew are read anew under a byte ranking, over the columns they were counted in.
        FollowTimelineBytes();
    }

    /// <summary>
    /// Process lanes in the ranked table's order over the whole session: most own records first, then by instance, so
    /// each lane sits where its row does. A brush reranks the table, not the lanes, and the timeline keeps its shape
    /// (§6.4); a lane of an instance the snapshot does not name goes last.
    /// </summary>
    private IReadOnlyList<ProcessTimelineLane> OrderProcessLanes(IReadOnlyList<ProcessTimelineLane> lanes)
    {
        Dictionary<ProcessInstanceId, long> records = wholeSnapshot.Processes
            .ToDictionary(process => process.Id, process => process.Records);
        return [.. lanes
            .OrderBy(lane => records.ContainsKey(lane.ProcessId) ? 0 : 1)
            .ThenByDescending(lane => records.GetValueOrDefault(lane.ProcessId))
            .ThenBy(lane => lane.ProcessId.ToString(), StringComparer.Ordinal)];
    }

    private bool HasCompleteLaneDetail => timelineDetail is { } detail
        && detail.MechanismLanes.Count == wholeSnapshot.MechanismLanes.Count
        && wholeSnapshot.MechanismLanes.All(lane =>
            detail.MechanismLanes.Any(candidate => candidate.Mechanism == lane.Mechanism));

    private void RefreshIntervalRows(IReadOnlyList<TimelineBucket>? focus = null)
    {
        Mechanism? selected = ShowsMechanismLanes ? selectedTimelineLane.Mechanism : null;
        ProcessTimelineLane? processLane = SelectedProcessLane;
        DirectionTimelineLane? directionLane = SelectedDirectionBucketLane;
        ChannelEndTimelineLane? endLane = SelectedChannelEndLane;
        IReadOnlyList<TimelineBucket> buckets = endLane is not null
            ? endLane.Buckets
            : directionLane is not null
            ? directionLane.Buckets
            : processLane is not null
            ? processLane.Buckets
            : selected is { } mechanism
            ? (HasCompleteLaneDetail
                ? timelineDetail!.MechanismLanes.First(lane => lane.Mechanism == mechanism).Buckets
                : wholeSnapshot.MechanismLanes.First(lane => lane.Mechanism == mechanism).Buckets)
            : timelineDetail?.Buckets ?? wholeSnapshot.Timeline;
        bool listsWhole = selected is null && processLane is null && directionLane is null && endLane is null;

        // A real session's rows state the bytes their own records sent and received, read for exactly what is listed.
        IntervalBytesRequest? listed = ReadsBytes && buckets.Count > 0
            ? new(new TimeRange(buckets[0].Interval.StartTicks, buckets[^1].Interval.EndTicks), buckets.Count,
                ScopeOfLane(selected, processLane?.ProcessId, directionLane?.Direction, endLane))
            : null;
        intervalRowsBesideFocus = listsWhole && focus is not null;
        FollowIntervalBytes(listed);
        intervals = WorkspaceRowBuilder.Intervals(buckets, ThemeResources.CurrentMode, listsWhole ? focus : null,
            IntervalTableShowsBytes, listed is { } request ? bucket => IntervalBytesText(request, bucket) : null);
        if (selectedIntervalRow is { } row)
        {
            // The analysis interval stays selected. Its row follows a focus count arriving at the same resolution, and
            // only a row gone at a new resolution is dropped.
            selectedIntervalRow = intervals.FirstOrDefault(candidate => candidate.Interval == row.Interval);
            OnPropertyChanged(nameof(SelectedIntervalRow));
        }

        OnPropertyChanged(nameof(Intervals));
        OnPropertyChanged(nameof(IntervalTableScope));
    }

    private void SetTimelineFocusState(bool loading, string? problem)
    {
        if (timelineFocusLoading == loading && timelineFocusProblem == problem)
        {
            return;
        }

        timelineFocusLoading = loading;
        timelineFocusProblem = problem;
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(TimelineShowsFocus));
        OnPropertyChanged(nameof(ShowsDirectionLanes));
        OnPropertyChanged(nameof(ShowsChannelEndLanes));
    }

    /// <summary>
    /// Follows the selection with its highlight (§6.4): a process's own records, a group's or an aggregate's members', or
    /// the channel chosen among a process's rows, whenever that is not already what the rung draws in colour. Selecting
    /// highlights; only a descent or Focus filters.
    /// </summary>
    private void UpdateHighlight()
    {
        (TimelineFocus? focus, string? name) = SelectionFocus();
        if (focus is not null && focus.Key == timelineFocus?.Key)
        {
            (focus, name) = (null, null);
        }

        if (focus?.Key == highlight?.Key)
        {
            return;
        }

        highlight = focus;
        highlightName = name;
        requestedHighlight = null;

        // The previous selection's counts describe other records.
        SetHighlight(null, null);
        OnPropertyChanged(nameof(TimelineHighlightName));
        OnPropertyChanged(nameof(TimelineHighlightBuckets));
        OnPropertyChanged(nameof(TimelineHighlightLanes));
        OnPropertyChanged(nameof(TimelineCaption));
        RequestHighlight();
    }

    /// <summary>
    /// The selected entity's records as a timeline focus, and its name; none for an empty selection. A row chosen among a
    /// process's rows is the latest choice there, so its records are highlighted rather than the process the rung shows.
    /// </summary>
    private (TimelineFocus? Focus, string? Name) SelectionFocus()
    {
        if (evidenceSource is null)
        {
            return (null, null);
        }

        if (chosenProcesses.Count > 0)
        {
            ProcessInstanceId[] members = [.. ChosenProcesses.Select(process => process.Id)];
            return members.Length == 0 ? (null, null) : (new(null, members), ProcessSetFilter.Label(members.Length));
        }

        // A chosen relationship's records: its linked calls, read by its own key, or the one channel it rests on.
        if (selectedRelationship is { } relationship)
        {
            string name = $"{relationship.Source} ↔ {relationship.Target}";
            return RpcChannelKeys.IsRpc(relationship.Key) ? (new(null, [], relationship.Key), name)
                : chosenChannelKey is { } only ? (new(only, []), name)
                : (null, null);
        }

        if (ChosenRow is { } row)
        {
            return (ChosenRowFocus(row.Row), row.Records.Label);
        }

        if (SelectedCluster is { } cluster)
        {
            return cluster.Members.Count == 0 ? (null, null) : (new(null, cluster.Members), cluster.Label);
        }

        if (SelectedGroup is { } group)
        {
            ProcessInstanceId[] members = [.. wholeSnapshot.Processes
                .Where(process => string.Equals(process.GroupKey, group.Key, StringComparison.Ordinal))
                .Select(process => process.Id)];
            return members.Length == 0 ? (null, null) : (new(null, members), group.Name);
        }

        return selectedProcess is { } selected ? (new(null, [selected.Id]), selected.NameWithPid) : (null, null);
    }

    /// <summary>
    /// Asks for the selection's records on the columns the timeline draws: the overview's at the whole extent, the zoomed
    /// detail's otherwise, so each highlight stands inside the bar drawn for its interval. A newer selection or view
    /// cancels an older count; a count that fails leaves nothing highlighted rather than a guess.
    /// </summary>
    private void RequestHighlight()
    {
        if (disposed || drawnTimeline is not { } drawn)
        {
            return;
        }

        if (highlight is null || evidenceSource is null)
        {
            CancelHighlight();
            requestedHighlight = null;
            SetHighlight(null, null);
            HighlightReady = Task.CompletedTask;
            return;
        }

        (TimeRange viewport, int columns) = drawn;
        TimeRange extent = wholeSnapshot.Extent;
        if (viewport.StartTicks <= extent.StartTicks && viewport.EndTicks >= extent.EndTicks && wholeSnapshot.Timeline.Count > 0)
        {
            viewport = extent;
            columns = wholeSnapshot.Timeline.Count;
        }

        (TimeRange, int, string) request = (viewport, columns, highlight.Key);
        if (requestedHighlight == request)
        {
            return;
        }

        requestedHighlight = request;
        CancelHighlight();
        var query = new CancellationTokenSource();
        highlightQuery = query;
        HighlightReady = LoadHighlightAsync(evidenceSource, viewport, columns, highlight, query);
    }

    private async Task LoadHighlightAsync(
        SessionEvidenceSource source, TimeRange viewport, int columns, TimelineFocus focus, CancellationTokenSource query)
    {
        try
        {
            SessionFocusedTimeline counted = await source.FocusedTimelineAsync(viewport, columns, focus, query.Token)
                .AnsweredLater();
            if (!disposed && ReferenceEquals(highlightQuery, query))
            {
                bool sameSession = counted.Whole.SessionId == source.SessionId;
                SetHighlight(sameSession ? counted.Focus : null, sameSession ? counted.FocusLanes : null);
            }
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            // A newer selection, viewport or rung superseded this count.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && ReferenceEquals(highlightQuery, query))
            {
                SetHighlight(null, null);
            }
        }
    }

    private void CancelHighlight()
    {
        highlightQuery?.Cancel();
        highlightQuery?.Dispose();
        highlightQuery = null;
    }

    private void SetHighlight(IReadOnlyList<TimelineBucket>? buckets, IReadOnlyList<MechanismTimelineLane>? lanes)
    {
        if (ReferenceEquals(highlightBuckets, buckets) && ReferenceEquals(highlightLanes, lanes))
        {
            return;
        }

        highlightBuckets = buckets;
        highlightLanes = lanes;
        OnPropertyChanged(nameof(TimelineHighlightBuckets));
        OnPropertyChanged(nameof(TimelineHighlightLanes));
        OnPropertyChanged(nameof(TimelineCaption));
    }

    /// <summary>
    /// The selection's records, one per bucket drawn over the same interval, when the selection is not the rung's focus:
    /// null without such a selection and until its count arrives.
    /// </summary>
    public IReadOnlyList<TimelineBucket>? TimelineHighlightBuckets => highlight is null ? null : highlightBuckets;

    /// <summary>The same records split by mechanism, for the machine rung's mechanism lanes.</summary>
    public IReadOnlyList<MechanismTimelineLane>? TimelineHighlightLanes => highlight is null ? null : highlightLanes;

    /// <summary>What the highlight marks, as the hover card and caption name it; null without a highlight.</summary>
    public string? TimelineHighlightName => highlight is null ? null : highlightName;

    /// <summary>Completes when the latest highlight count has been applied or given up.</summary>
    internal Task HighlightReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The records the rung's timeline draws in colour: what E reads from this rung (§3.2), so a group's timeline shows
    /// its members' records, an instance its own and a channel its two ends'. The machine rung, a rung whose filters were
    /// removed and a workspace with no published session have no focus, and draw every record in its mechanism's hue.
    /// </summary>
    private void UpdateTimelineFocus()
    {
        EvidenceScope scope = EvidenceScopes.Resolve(wholeSnapshot, ladder.Current with { Viewport = wholeSnapshot.Extent });
        TimelineFocus? focus = evidenceSource is null ? null : TimelineFocus.Of(scope);
        if (focus?.Key == timelineFocus?.Key)
        {
            return;
        }

        timelineFocus = focus;
        timelineFocusDescription = focus is null ? null : scope.Description;
        timelineFocusLoading = false;
        timelineFocusProblem = null;

        // An end is a place on one channel: the first end of another channel is unrelated to it.
        if (selectedChannelEnd is not null)
        {
            selectedChannelEnd = null;
            OnPropertyChanged(nameof(SelectedChannelEnd));
            OnPropertyChanged(nameof(SelectedChannelEndOption));
        }

        // The previous focus's counts describe other records; the whole timeline beside them is still true.
        SetTimelineDetail(timelineDetail, null);
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(TimelineShowsFocus));

        // A selection that is now the rung's own focus is drawn in colour, not highlighted over it.
        UpdateHighlight();
        if (drawnTimeline is { } drawn)
        {
            RequestTimelineDetail(drawn.Viewport, drawn.Columns);
        }
    }

    /// <summary>What this workspace's ranking counted, for the next publication of the same session to show until its own arrive.</summary>
    public ScopeCarry CarryScope() => new(visibleRange, displayedCountsInterval, displayedCounts,
        byteReads.Whole ?? byteReads.CarriedWhole, byteReads.Interval ?? byteReads.CarriedInterval,
        callReads.Whole ?? callReads.CarriedWhole, callReads.Interval ?? callReads.CarriedInterval,
        peerReads.Whole ?? peerReads.CarriedWhole, peerReads.Interval ?? peerReads.CarriedInterval);

    /// <summary>
    /// Takes on an earlier publication's visible range, so this generation starts counting it before the view is bound,
    /// and shows that publication's counts while this one's own are read. Nothing stands in once they have arrived, or
    /// when the scope is the whole session. A ranking's bytes or calls stand in the same way.
    /// </summary>
    public void AdoptScope(ScopeCarry carry)
    {
        ArgumentNullException.ThrowIfNull(carry);
        if (evidenceSource is { } current)
        {
            byteReads.Adopt(carry.WholeBytes, carry.IntervalBytes, current.SessionId);
            callReads.Adopt(carry.WholeCalls, carry.IntervalCalls, current.SessionId);
            peerReads.Adopt(carry.WholePeers, carry.IntervalPeers, current.SessionId);
        }

        ShowVisibleRange(carry.VisibleRange);
        if (carry.Counts is { } counts && evidenceSource is { } source && counts.SessionId == source.SessionId
            && scopedInterval is not null && intervalLoading && scopedSnapshot is null)
        {
            ApplyScope(counts, carry.Interval, standIn: true);
            OnPropertyChanged(nameof(RankingScopeText));
        }

        // Carried measures rank the rows at once where they answer the scope shown, while this generation's are read.
        Rerank();
        RankingReady = FollowRankedMeasuresAsync();
    }

    /// <summary>What this workspace's timeline drew, for the next publication of the same session to show until its own counts arrive.</summary>
    public TimelineCarry CarryTimeline() => new(timelineDetail, timelineFocus?.Key, timelineFocusBuckets,
        timelineProcessLanes, processLaneProblem, timelineDirectionLanes, timelineChannelEnds,
        highlight?.Key, highlightBuckets, highlightLanes, overviewLaneBytes?.Measures, zoomedLaneBytes?.Measures,
        ownerLaneBytes?.Measures, directionLaneBytes?.Measures);

    /// <summary>
    /// Shows an earlier publication's zoomed detail and focus counts until this generation's own arrive, so a live
    /// refresh does not blink the timeline back to coarse bars or drop the focus's colour for the length of a count.
    /// The focus counts are kept only for the same focus; a stand-in is replaced, never merged.
    /// </summary>
    public void AdoptTimeline(TimelineCarry carry)
    {
        ArgumentNullException.ThrowIfNull(carry);
        IReadOnlyList<TimelineBucket>? focus = carry.FocusKey is not null && carry.FocusKey == timelineFocus?.Key ? carry.Focus : null;
        bool sameFocus = carry.FocusKey is not null && carry.FocusKey == timelineFocus?.Key;
        SetTimelineDetail(carry.Detail, focus, sameFocus ? carry.ProcessLanes : null,
            sameFocus ? carry.ProcessLaneProblem : null, sameFocus ? carry.DirectionLanes : null,
            sameFocus ? carry.ChannelEndLanes : null);

        // The selection's highlight stands in the same way, for the same selection only, until its own count arrives.
        if (carry.HighlightKey is not null && carry.HighlightKey == highlight?.Key && highlightBuckets is null)
        {
            SetHighlight(carry.Highlight, carry.HighlightLanes);
        }

        // The lanes' bytes stand in the same way, so a live refresh under a byte ranking does not blink back to records.
        AdoptTimelineBytes(carry, sameFocus);
        OnPropertyChanged(nameof(TimelineCaption));
    }

    /// <summary>
    /// The focus's counts, one per bucket of the whole timeline drawn over the same interval: the overview's buckets at
    /// the whole extent, the zoomed detail's when zoomed. Null at a rung without a focus and until its count arrives.
    /// </summary>
    public IReadOnlyList<TimelineBucket>? TimelineFocusBuckets => timelineFocusBuckets;

    /// <summary>Exact L1 owner rows held with their focus and generation, ready for the rung renderer.</summary>
    public IReadOnlyList<ProcessTimelineLane>? TimelineProcessLanes => timelineProcessLanes;

    /// <summary>Exact L2 source-direction rows held with their single-owner focus and generation.</summary>
    public IReadOnlyList<DirectionTimelineLane>? TimelineDirectionLanes => timelineDirectionLanes;

    public bool ShowsDirectionLanes => ladder.Current.Level == DetailLevel.ProcessInstance
        && timelineDirectionLanes is { Count: > 0 } && timelineFocusProblem is null;

    private DirectionTimelineLane? SelectedDirectionBucketLane => ShowsDirectionLanes
        && selectedDirectionLane.Direction is { } direction
            ? timelineDirectionLanes!.FirstOrDefault(lane => lane.Direction == direction) : null;

    public IReadOnlyList<DirectionLaneOption> DirectionLaneOptions => directionLaneOptions;

    public DirectionLaneOption SelectedDirectionLane
    {
        get => selectedDirectionLane;
        set
        {
            if (value is null || !directionLaneOptions.Contains(value) || selectedDirectionLane == value) return;
            selectedDirectionLane = value;
            RefreshIntervalRows(timelineFocusBuckets);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimelineCaption));
        }
    }

    public Direction? SelectedTimelineDirection => selectedDirectionLane.Direction;

    public void SelectDirectionLane(Direction? direction)
    {
        if (directionLaneOptions.FirstOrDefault(option => option.Direction == direction) is { } option)
            SelectedDirectionLane = option;
    }

    /// <summary>
    /// What a live capture has journaled after the chunks this workspace shows, on the timeline's own axis; null when
    /// nothing is live, the view is paused or held, or there is nothing yet to preview (§12, §19.3).
    /// </summary>
    public LiveEdge? LiveEdge => liveEdge;

    /// <summary>
    /// Shows the broker's live preview for the chunks after <paramref name="shownChunks"/>, or none. It never changes a
    /// published count, a selection or a table: a preview is drawn and explained, nothing more.
    /// </summary>
    public void ShowLivePreview(BrokerCapturePreview? preview, int shownChunks)
    {
        LiveEdge? edge = preview is not null && wholeSnapshot.Clock is { } clock
            ? LiveEdge.From(preview, shownChunks, clock)
            : null;
        if (edge is null && liveEdge is null)
        {
            return;
        }

        liveEdge = edge;
        OnPropertyChanged(nameof(LiveEdge));
    }

    /// <summary>A live edge bin's card: what it counts, where the counts come from, and what they are not yet (§19.3).</summary>
    public HoverCard DescribeLiveEdgeHover(LiveEdgeBin bin, Mechanism? lane = null)
    {
        ArgumentNullException.ThrowIfNull(bin);
        int count = lane is { } mechanism ? bin.CountOf(mechanism) : bin.Total;
        var lines = new List<string>
        {
            (count == 0 ? "No record" : Counted(count, "record", "records")) + " journaled here and not yet published"
                + (lane is { } laneMechanism ? $" · {EvidenceRowText.MechanismName(laneMechanism)} lane" : string.Empty),
            "By mechanism: " + string.Join(" · ", bin.Counts.Select(entry => string.Create(CultureInfo.CurrentCulture,
                $"{EvidenceRowText.MechanismName(entry.Mechanism)} {entry.Count:N0}"))),
            "Basis: live preview · unit: records · counted by the broker as each record was journaled, before derivation",
            "Exact records replace this preview when the chunk holding them is published and derived here, "
                + "about every 2 s while recording",
            "Not yet attributed to processes or channels, and not in tables, rankings, selections or exports",
        };
        if (liveEdge is { UncoveredChunks: > 0 } behind)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture,
                $"{behind.UncoveredChunks:N0} published chunks are neither shown nor previewed yet: this view is behind the capture"));
        }

        if (liveEdge is { UnbinnedRecords: > 0 } unbinned)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture,
                $"Up to {unbinned.UnbinnedRecords:N0} previewed records fall outside the preview's bins"));
        }

        return new(WorkspaceTime.FormatHalfOpenRange(bin.Interval, CultureInfo.CurrentCulture) + " · live preview", lines);
    }

    /// <summary>The focused channel's two ends, first end first, held with their focus and generation.</summary>
    public IReadOnlyList<ChannelEndTimelineLane>? TimelineChannelEndLanes => timelineChannelEnds;

    /// <summary>Whether the channel rung draws one lane per end, banded by direction (§3.2 L3).</summary>
    public bool ShowsChannelEndLanes => ladder.Current.Level == DetailLevel.Channel
        && timelineChannelEnds is { Count: > 0 } && timelineFocusProblem is null;

    private ChannelEndTimelineLane? SelectedChannelEndLane => ShowsChannelEndLanes && selectedChannelEnd is { } end
        ? timelineChannelEnds!.FirstOrDefault(lane => lane.End == end) : null;

    /// <summary>Both ends, then each end by its holder and endpoint: the table equivalent of the end lanes' names.</summary>
    public IReadOnlyList<ChannelEndOption> ChannelEndOptions => channelEndOptions;

    public ChannelEndOption SelectedChannelEndOption
    {
        get => channelEndOptions.FirstOrDefault(option => option.End == selectedChannelEnd) ?? channelEndOptions[0];
        set
        {
            if (value is not null && channelEndOptions.Contains(value)) SelectChannelEnd(value.End);
        }
    }

    /// <summary>The end chosen for the interval table and bracket stepping: 0 the first end, 1 the second.</summary>
    public int? SelectedChannelEnd => selectedChannelEnd;

    public void SelectChannelEnd(int? end)
    {
        if (end is not (null or 0 or 1) || selectedChannelEnd == end) return;
        selectedChannelEnd = end;
        RefreshIntervalRows(timelineFocusBuckets);
        OnPropertyChanged(nameof(SelectedChannelEnd));
        OnPropertyChanged(nameof(SelectedChannelEndOption));
        OnPropertyChanged(nameof(TimelineCaption));
    }

    /// <summary>An end as a person reads it: who holds it and its own endpoint, which tells a looped process's ends apart.</summary>
    public string ChannelEndLabel(ChannelEndTimelineLane end) => $"{ChannelEndHolder(end)} · {end.Endpoint}";

    /// <summary>The process that holds an end, by name and PID; an instance this snapshot does not list, by its ID.</summary>
    public string ChannelEndHolder(ChannelEndTimelineLane end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return wholeSnapshot.Processes.FirstOrDefault(process => process.Id == end.Holder)?.NameWithPid
            ?? "instance " + end.Holder.ToString()[..8];
    }

    /// <summary>L1 process rows ordered by PID and stable instance ID, independent of query/ranking order.</summary>
    public IReadOnlyList<ProcessTimelineLane> ProcessLaneDisplay => processLaneDisplay;

    public bool ShowsProcessLanes => ladder.Current.Level == DetailLevel.Group && processLaneDisplay.Count > 0
        && timelineFocusProblem is null;

    /// <summary>
    /// The columns the process lanes were counted in when fewer than the view draws: past the cell budget a group's lanes
    /// are counted coarser rather than dropped (§6.2); null when they share the view's columns.
    /// </summary>
    private int? CoarserLaneColumns => processLaneDisplay.Count > 0
        && processLaneDisplay[0].Buckets.Count < (timelineDetail?.Buckets.Count ?? wholeSnapshot.Timeline.Count)
            ? processLaneDisplay[0].Buckets.Count
            : null;

    /// <summary>What the lanes' caption adds when they are counted coarser than the view: their columns and why.</summary>
    private string LaneResolutionNote => CoarserLaneColumns is { } lanes
        ? string.Create(CultureInfo.CurrentCulture,
            $" in {lanes:N0} columns, coarser than the view, to stay within {SessionTimelineQuery.MaximumProcessLaneCells:N0} cells")
        : string.Empty;

    private ProcessTimelineLane? SelectedProcessLane => ShowsProcessLanes && selectedProcess is { } process
        ? processLaneDisplay.FirstOrDefault(lane => lane.ProcessId == process.Id) : null;

    public bool HasSelectedProcessLane => SelectedProcessLane is not null;

    public void ClearProcessLaneFocus()
    {
        SelectedRung = null;
        SelectedProcess = null;
    }

    /// <summary>When an L1 group exceeds the query budget, the exact aggregate remains and this says why.</summary>
    public string? ProcessLaneProblem => processLaneProblem;

    /// <summary>All mechanism rows are reachable from the table; selecting one does not filter the graph or ranking.</summary>
    public IReadOnlyList<TimelineLaneOption> TimelineLaneOptions => timelineLaneOptions;

    public TimelineLaneOption SelectedTimelineLane
    {
        get => selectedTimelineLane;
        set
        {
            if (value is null || !timelineLaneOptions.Contains(value) || selectedTimelineLane == value) return;
            selectedTimelineLane = value;
            RefreshIntervalRows(timelineFocusBuckets);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimelineCaption));
        }
    }

    public Mechanism? SelectedTimelineMechanism => selectedTimelineLane.Mechanism;

    public void SelectTimelineLane(Mechanism? mechanism)
    {
        if (timelineLaneOptions.FirstOrDefault(option => option.Mechanism == mechanism) is { } option)
            SelectedTimelineLane = option;
    }

    /// <summary>L0 is the only rung with exact mechanism lane data so far.</summary>
    public bool ShowsMechanismLanes => ladder.Current.Level == DetailLevel.Machine
        && wholeSnapshot.MechanismLanes.Count > 0;

    /// <summary>Whether the timeline draws the rung's focus in colour over the rest of the machine in grey.</summary>
    public bool TimelineShowsFocus => timelineFocus is not null && timelineFocusProblem is null;

    /// <summary>What the timeline draws, stated above it: every record, or the rung's focus over the rest of the machine.</summary>
    public string TimelineCaption => RungCaption + LaneRecordsNote + HighlightCaption;

    /// <summary>
    /// What the selection's highlight adds to the caption: what it marks, or that it is still being counted. Its share is
    /// a count of records, so lanes plotting bytes do not draw it on their scale.
    /// </summary>
    private string HighlightCaption => highlight is null ? string.Empty
        : timelineBytes is not null ? $" · {highlightName} selected, highlighted while the lanes plot records"
        : highlightBuckets is null ? $" · counting the selection, {highlightName}…"
        : $" · selection highlighted: {highlightName}";

    private string RungCaption => ShowsRpcCallLane
        ? RpcCallDensity is { } density
            ? string.Create(CultureInfo.CurrentCulture, $"This channel's {rpcSpans!.Total:N0} calls in view, as density: ")
                + string.Create(CultureInfo.CurrentCulture, $"the taller a column, the more calls ran in it, up to {density.Maximum:N0}; ")
                + "failures in caution ink · machine context above · zoom in to see each call, or click a column to select its interval"
            : "This channel's calls, each from its start to its stop: failures in caution ink, calls open at capture end "
                + "faint · machine context above · click a call to select its row"
        : ShowsHttpExchangeLane
        ? HttpExchangeDensity is { } exchanges
            ? string.Create(CultureInfo.CurrentCulture, $"This process's {httpSpans!.Total:N0} HTTP exchanges in view, as density: ")
                + string.Create(CultureInfo.CurrentCulture, $"the taller a column, the more exchanges ran in it, up to {exchanges.Maximum:N0}; ")
                + "the share not recorded whole faint · machine context above · zoom in to see each exchange, or click a column "
                + "to select its interval"
            : "This process's HTTP exchanges, each from its first buffer to its response's end: faint where not recorded whole "
                + "· machine context above · click an exchange to select its row"
        : timelineFocusDescription is not { } focus
        ? ShowsMechanismLanes
            ? (timelineBytes is { } plotted
                    ? $"{Capitalized(Phrase(plotted.Metric))} per second by mechanism · "
                    : "Observed records by mechanism · " + (LaneBytesNote is { } reading ? reading + " · " : string.Empty))
                + Counted(wholeSnapshot.MechanismLanes.Count, "lane", "lanes")
                + (timelineDrawsUnmeasured ? " · cross-hatched where no size was recorded" : string.Empty)
                + (SelectedTimelineMechanism is { } mechanism
                    ? $" · {EvidenceRowText.MechanismName(mechanism)} table/step focus"
                    : " · click a lane name or choose one in tables (T)")
                + " · Shift+drag brushes"
            : "Observed records · Shift+drag brushes a range · unknown stays unknown"
        : timelineFocusProblem is { } problem
            ? $"{focus} could not be counted: {problem.TrimEnd('.')}. Every observed record is shown."
            : ladder.Current.Level == DetailLevel.Group && processLaneProblem is { } laneProblem
                ? $"{focus} · process lanes unavailable: {laneProblem}"
            : ShowsProcessLanes
                ? $"{focus} · {Counted(processLaneDisplay.Count, "process lane", "process lanes")}" + LaneResolutionNote
                    + ProcessLaneBytesNote + " · machine context above · scroll names for more"
            : ShowsDirectionLanes
                ? $"{focus} · by source direction" + DirectionLaneBytesNote + " · machine context above"
                    + (SelectedTimelineDirection is { } direction
                        ? $" · {DirectionLabel(direction)} table/step focus"
                        : " · click a lane name or choose one in tables (T)")
            : ShowsChannelEndLanes
                ? $"{focus} · one lane per end: outbound above its midline, inbound below · machine context above"
                    + (SelectedChannelEndLane is { } end
                        ? $" · {ChannelEndLabel(end)} table/step focus"
                        : " · click an end's name or choose one in tables (T)")
            : timelineFocusLoading && timelineFocusBuckets is null
                ? $"Counting {char.ToLowerInvariant(focus[0])}{focus[1..]}… · the rest of the machine in grey"
                : $"{focus} in colour, the rest of the machine in grey · Shift+drag brushes a range";

    /// <summary>Whether the ranking counts an interval - the brush or the visible range - rather than the whole session.</summary>
    public bool IsRankedWithinInterval => scopedSnapshot is not null;

    /// <summary>
    /// The time the ranking, graph, tables, inspector and E answer (§6.4): the brushed interval if there is one, else the
    /// visible range while the timeline is zoomed, else the whole session (null). Only a published session is counted
    /// by interval; the tour always answers for all of it.
    /// </summary>
    public TimeRange? ScopeInterval => selectedInterval ?? VisibleScope;

    /// <summary>Whether the scope is the visible range, which follows zoom and pan until it is kept.</summary>
    public bool ScopeFollowsView => selectedInterval is null && VisibleScope is not null;

    /// <summary>§6.4's scope lock is offered while the scope follows the view: it keeps that range as the interval.</summary>
    public bool CanKeepVisibleRange => ScopeFollowsView;

    /// <summary>Whether the rail states a scope: one is being counted, has been applied, or could not be counted.</summary>
    public bool ShowsRankingScope => evidenceSource is not null && scopedInterval is not null;

    private TimeRange? VisibleScope => evidenceSource is null ? null : visibleRange;

    /// <summary>
    /// The timeline's settled viewport (§6.4). A range narrower than the extent becomes the scope while nothing is
    /// brushed; null or the whole extent means everything is visible, and the scope returns to the whole session.
    /// </summary>
    public void ShowVisibleRange(TimeRange? range)
    {
        TimeRange extent = wholeSnapshot.Extent;
        TimeRange? next = null;
        if (range is { } visible && (visible.StartTicks > extent.StartTicks || visible.EndTicks < extent.EndTicks))
        {
            long start = Math.Max(visible.StartTicks, extent.StartTicks);
            long end = Math.Min(visible.EndTicks, extent.EndTicks);
            next = end > start ? new TimeRange(start, end) : null;
        }

        if (disposed || next == visibleRange)
        {
            return;
        }

        visibleRange = next;
        SyncIntervalScope();
        RaiseScopeChanged();
    }

    /// <summary>
    /// The scope lock (§6.4): keeps the visible range as the analysis interval, so zooming and panning to look around no
    /// longer change what the ranking counts. The kept range is drawn on the axis and cleared like any brush.
    /// </summary>
    public bool KeepVisibleRange()
    {
        if (!ScopeFollowsView || VisibleScope is not { } visible)
        {
            return false;
        }

        SelectInterval(visible);
        return true;
    }

    private void RaiseScopeChanged()
    {
        OnPropertyChanged(nameof(ScopeInterval));
        OnPropertyChanged(nameof(ScopeFollowsView));
        OnPropertyChanged(nameof(CanKeepVisibleRange));
        OnPropertyChanged(nameof(ShowsRankingScope));
        OnPropertyChanged(nameof(RankingScopeText));
        OnPropertyChanged(nameof(IntervalLabel));
    }

    /// <summary>What the ranking counts, stated wherever totals appear so a brushed number is never read as a session total.</summary>
    public string RankingScopeText
    {
        get
        {
            if (evidenceSource is null || scopedInterval is not { } interval) return string.Empty;
            string range = WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture);
            bool visible = selectedInterval is null;
            string where = visible ? $"the visible {range}" : range;
            return intervalProblem is { } problem ? $"Could not rank within {where}: {problem}"
                : intervalLoading && scopeStandsIn ? $"Ranking within {where}… · the previous publication's counts until then"
                : intervalLoading ? $"Ranking within {where}…"
                : visible ? $"Ranked within {where} · follows zoom and pan until you keep it"
                : $"Ranked within {range} · Esc at the machine rung clears it";
        }
    }

    public string WorkspaceDisclosure => emptyWorkspace && awaitingCaptureNote is { } note ? note : workspaceDisclosure;

    /// <summary>What the workspace is called in the header: its snapshot's title, or a waiting capture's state.</summary>
    public string Title => emptyWorkspace && awaitingCaptureTitle is { } title ? title : Snapshot.Title;

    /// <summary>Whether this is the workspace shown before any session: no capture published and nothing opened.</summary>
    internal bool IsEmptyWorkspace => emptyWorkspace;

    /// <summary>
    /// Whether the rail shows the ranked table's heading, summary, hints and search: not while no session is shown, when
    /// the saved sessions stand where the ranked table will be (§3.1 step 1) and there is nothing to rank or search.
    /// </summary>
    public bool ShowsRankedHeader => !emptyWorkspace;

    /// <summary>
    /// What an empty workspace says while a capture starts or records and has published nothing yet (§6.8's designed
    /// states): that it is recording and when the first view comes. Null outside a capture, when it says none is running.
    /// </summary>
    public string? AwaitingCaptureNote => awaitingCaptureNote;

    /// <summary>
    /// Names a waiting capture's state in an empty workspace's header and says what is happening in its empty rung and
    /// disclosure; nulls restore the words for no capture. A workspace holding a session ignores it.
    /// </summary>
    public void SetAwaitingCapture(string? title, string? note)
    {
        if (!emptyWorkspace || (string.Equals(title, awaitingCaptureTitle, StringComparison.Ordinal)
            && string.Equals(note, awaitingCaptureNote, StringComparison.Ordinal)))
        {
            return;
        }

        awaitingCaptureTitle = title;
        awaitingCaptureNote = note;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AwaitingCaptureNote));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(WorkspaceDisclosure));
    }

    /// <summary>Captures navigation independently of an evidence generation, for a same-session refresh only.</summary>
    public WorkspaceNavigationMemento CaptureNavigation() => new(
        [.. ladder.Breadcrumb.Select(rung => rung with { Filters = [.. rung.Filters] })],
        selectedProcess?.Id, selectedInterval, selectedRung?.Key, showTables, selectedClusterKey,
        searchText, selectedSearchResult?.Hit.Key, SelectedTimelineMechanism, SelectedTimelineDirection,
        selectedChannelEnd, [.. ladder.Forward.Select(rung => rung with { Filters = [.. rung.Filters] })], rankBy, perSecond,
        scalesEachLane);

    /// <summary>
    /// Replays stable focus keys against this generation, never a row index. If an entity vanished, stops at the
    /// nearest surviving ancestor and says so. A whole-extent viewport follows the new extent; a deliberate brush
    /// keeps its exact half-open range where retained, or reports clipping (R7, I3).
    /// </summary>
    public string? RestoreNavigation(WorkspaceNavigationMemento saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Breadcrumb.Count == 0)
        {
            throw new ArgumentException("Navigation needs a machine rung.", nameof(saved));
        }
        if (ladder.Depth != 0)
        {
            throw new InvalidOperationException("Navigation can only be restored into a fresh workspace.");
        }
        ShowTables = saved.ShowTables;
        NavigationState[] old = [.. saved.Breadcrumb];
        if (old[0].Level != DetailLevel.Machine || old[0].Focus is not null)
        {
            throw new ArgumentException("Navigation must begin at the machine rung.", nameof(saved));
        }
        TimeRange previousExtent = old[0].Viewport;
        var notices = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < old.Length; index++)
        {
            NavigationState previous = old[index - 1];
            NavigationState target = old[index];
            TimeRange viewport = Rebase(target.Viewport, previousExtent, Snapshot.Extent,
                out bool clipped, out bool lost);
            if (lost)
            {
                notices.Add("The previous time viewport is no longer retained; showing the current extent.");
            }
            else if (clipped)
            {
                notices.Add("The previous time viewport was clipped to retained evidence.");
            }

            LadderDescent? descent = null;
            if (target.Level == DetailLevel.Evidence && target.Focus is { } evidenceFocus
                && target.Filters.LastOrDefault(filter => filter.Field == "scope") is { Level: { } scopeLevel } scopeFilter)
            {
                // An evidence rung is replayed with the exact scope it showed, including one reached from a channel
                // the discovery list chose; the reader then says if that scope is gone from this generation.
                descent = LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                    new(scopeLevel, evidenceFocus.Key, evidenceFocus.Label), scopeFilter.Reason);
            }
            else if (target.Level == DetailLevel.Evidence
                && (previous.Focus is null && target.Focus?.Key == "machine"
                    || previous.Focus is { } focus && target.Focus?.Key == focus.Key))
            {
                descent = LadderProjection.EvidenceDescentFor(ladder.Current, viewport);
            }
            else if (target.Focus is { } targetFocus)
            {
                // An RPC channel is read from the session rather than listed in the overview, so it is re-entered by its
                // key; its reader says when this generation no longer holds it.
                LadderRow? row = target.Level == DetailLevel.Channel && RpcChannelKeys.IsRpc(targetFocus.Key)
                    ? RpcChannelRow(targetFocus.Key, targetFocus.Label, string.Empty, 0)
                    : target.Level == DetailLevel.Channel && HttpExchangeKeys.IsHttp(targetFocus.Key)
                    ? HttpChannelRow(targetFocus.Key, targetFocus.Label, string.Empty, 0)
                    : LadderProjection.Project(Snapshot, ladder.Current).Rows.FirstOrDefault(
                        candidate => candidate.Key == targetFocus.Key && candidate.DescendsTo == target.Level);
                if (row is not null)
                {
                    descent = LadderProjection.DescentFor(row, ladder.Current, viewport);
                }
            }

            ladder.RecordInterval(Rebase(previous.IntervalWhenLeft, previousExtent));
            if (descent is null || !ladder.TryDescend(descent, out _))
            {
                notices.Add($"The previous {NavigationState.Name(target.Level).ToLowerInvariant()} focus is not in this "
                    + "generation; returned to the nearest available rung.");
                break;
            }

            foreach (ImpliedFilter filter in ladder.Current.Filters.ToArray())
            {
                if (!target.Filters.Any(oldFilter => oldFilter.Field == filter.Field))
                {
                    _ = ladder.TryRemoveFilter(filter.Field);
                }
            }
        }

        if (ladder.Depth == old.Length - 1 && saved.Forward is { Count: > 0 } savedForward)
        {
            RestoreForward(savedForward, previousExtent);
        }

        AfterNavigation();
        RankBy = saved.RankBy;
        PerSecond = saved.PerSecond;
        ScalesEachLane = saved.ScalesEachLane;
        if (saved.SelectedTimelineMechanism is { } savedMechanism)
        {
            if (timelineLaneOptions.Any(option => option.Mechanism == savedMechanism))
            {
                SelectTimelineLane(savedMechanism);
            }
            else
            {
                notices.Add("The selected mechanism lane is not observed in this generation; showing all mechanisms.");
            }
        }
        if (saved.SelectedTimelineDirection is { } savedDirection)
            SelectDirectionLane(savedDirection);
        if (saved.SelectedChannelEnd is { } savedEnd)
            SelectChannelEnd(savedEnd);
        SelectedRung = ladder.Depth == old.Length - 1
            ? RungRows.FirstOrDefault(row => row.Key == saved.SelectedRungKey)
            : null;
        if (IsEvidenceRung && ladder.Depth == old.Length - 1)
        {
            // Evidence rows arrive after the first page loads; the selection follows them if the row is still there.
            pendingEvidenceKey = saved.SelectedRungKey;
        }
        ProcessNode? process = Snapshot.Processes.FirstOrDefault(node => node.Id == saved.SelectedProcess);
        SelectedProcess = process;
        if (saved.SelectedProcess is not null && process is null)
        {
            notices.Add("The selected process is not in this generation; the selection was cleared.");
        }

        // An aggregate's key names its role (no relationships, other processes, an opened group's other members), so it
        // survives a publication whose aggregate has other members; one this generation no longer draws is not revived.
        if (saved.SelectedGraphAggregate is { } aggregate
            && graphDisplay.Node(aggregate) is { Kind: not (GraphNodeKind.Process or GraphNodeKind.Group) })
        {
            SelectGraphNode(aggregate);
        }

        if (saved.SelectedInterval is { } interval)
        {
            long start = Math.Max(interval.StartTicks, Snapshot.Extent.StartTicks);
            long end = Math.Min(interval.EndTicks, Snapshot.Extent.EndTicks);
            if (end <= start)
            {
                notices.Add("The selected time interval is outside retained evidence; its selection was cleared.");
            }
            else
            {
                if (start != interval.StartTicks || end != interval.EndTicks)
                {
                    notices.Add("The selected time interval was clipped to retained evidence.");
                }

                SelectInterval(new(start, end));
            }
        }

        SearchText = saved.SearchText;
        if (saved.SelectedSearchKey is { } searchKey
            && searchRows.FirstOrDefault(row => row.Hit.Key == searchKey) is { } hit)
        {
            SelectedSearchResult = hit;
        }

        return notices.Count == 0 ? null : string.Join(" ", notices);
    }

    /// <summary>A rung's remembered interval in this generation: clipped to retained evidence, or none when it is gone.</summary>
    private TimeRange? Rebase(TimeRange? interval, TimeRange previousExtent)
    {
        if (interval is not { } old)
        {
            return null;
        }

        TimeRange rebased = Rebase(old, previousExtent, Snapshot.Extent, out _, out bool lost);
        return lost ? null : rebased;
    }

    /// <summary>
    /// Puts back the way forward that a publication carried, as far as this generation still has it: each rung must
    /// still be a row of the rung above it and its time must still be retained, and the way stops before the first
    /// that is not, so a forward step never lands somewhere the user did not leave.
    /// </summary>
    private void RestoreForward(IReadOnlyList<NavigationState> saved, TimeRange previousExtent)
    {
        var kept = new List<NavigationState>(saved.Count);
        NavigationState above = ladder.Current;
        foreach (NavigationState rung in saved)
        {
            TimeRange viewport = Rebase(rung.Viewport, previousExtent, Snapshot.Extent, out _, out bool lost);
            if (lost || !Reachable(rung, above, wholeSnapshot))
            {
                break;
            }

            NavigationState rebased = rung with
            {
                Viewport = viewport,
                Filters = [.. rung.Filters],
                IntervalWhenLeft = Rebase(rung.IntervalWhenLeft, previousExtent),
            };
            kept.Add(rebased);
            above = rebased;
        }

        _ = ladder.TryRestoreForward(kept);
    }

    /// <summary>
    /// Whether a rung can be entered from the one above it in a generation: it is still one of that rung's rows. The
    /// evidence rung is always reachable; its reader says when the records it names are gone.
    /// </summary>
    private static bool Reachable(NavigationState rung, NavigationState above, WorkspaceSnapshot snapshot) =>
        rung.Level == DetailLevel.Evidence
        || rung.Level == DetailLevel.Channel && RpcChannelKeys.IsRpc(rung.Focus?.Key)
        || rung.Focus is { } focus && LadderProjection.Project(snapshot, above).Rows.Any(
            row => string.Equals(row.Key, focus.Key, StringComparison.Ordinal) && row.DescendsTo == rung.Level);

    private static TimeRange Rebase(
        TimeRange old, TimeRange previousExtent, TimeRange currentExtent, out bool clipped, out bool lost)
    {
        if (old == previousExtent)
        {
            clipped = false;
            lost = false;
            return currentExtent;
        }

        long start = Math.Max(old.StartTicks, currentExtent.StartTicks);
        long end = Math.Min(old.EndTicks, currentExtent.EndTicks);
        clipped = start != old.StartTicks || end != old.EndTicks;
        lost = end <= start;
        return !lost ? new(start, end) : currentExtent;
    }

    /// <summary>
    /// Stable graph coordinates of every process, separate from evidence, time scope and the window transform. A process
    /// drawn inside a node that stands for several is at that node's position.
    /// </summary>
    public IReadOnlyDictionary<ProcessInstanceId, GraphPoint> GraphPositions { get; private set; }

    /// <summary>The graph as drawn (§6.3): every process is represented by exactly one node, within the display budget.</summary>
    /// <summary>
    /// The drawn graph, sized by the ranking's metric: under a byte ranking whose bytes are shown, each edge's thickness
    /// reads the bytes sent across it and each node's size the bytes its relationships carry, so the ranked table and the
    /// graph never disagree about magnitude (§6.3); otherwise by records.
    /// </summary>
    public GraphDisplay GraphDisplay => Weighted(graphDisplay);

    /// <summary>
    /// Whether the graph draws a mark whose magnitude was not measured - under a byte ranking, a relationship whose sends
    /// recorded no size - so the legend keys §6.6's unmeasured encoding beside it. A key shows only what a pane draws.
    /// </summary>
    public bool GraphDrawsUnmeasured => GraphDisplay is { } drawn
        && (drawn.Edges.Any(edge => edge.Unmeasured) || drawn.Nodes.Any(node => node.Unmeasured && node.Kind != GraphNodeKind.Context));

    /// <summary>
    /// The graph's scope in one short line for the pane header: how many processes, how many relationships and among how
    /// many of them, and how many nodes they are drawn as when groups are collapsed. Compaction is never called omission.
    /// </summary>
    public string GraphSummary
    {
        get
        {
            if (GraphLayoutProblem is not null && graphDisplay.Nodes.Count == 0)
            {
                return "Graph unavailable · the ranked table still lists every process";
            }

            int processes = wholeSnapshot.Processes.Count;
            if (processes == 0)
            {
                return "No process observed yet";
            }

            // A focused rung names its focus and how much of the machine it draws; the rest is one context node (§3.2).
            // Processes the focus folds, such as an opened group's members with no relationship, are drawn as fewer nodes.
            if (graphFocus.Neighborhood is { } drawn)
            {
                int outside = processes - drawn.Count;
                int nodes = graphDisplay.Nodes.Count(node => node.Kind != GraphNodeKind.Context);
                return $"{graphFocus.Description}: " + Counted(drawn.Count, "process", "processes") + " drawn"
                    + (nodes == drawn.Count ? string.Empty : " as " + Counted(nodes, "node", "nodes"))
                    + (outside > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {outside:N0} more in Rest of the machine") : string.Empty);
            }

            int relationships = wholeSnapshot.Edges.Count;
            string text = Counted(processes, "process", "processes") + " · " + (relationships == 0
                ? "no relationship drawn; observed records may still be in the timeline"
                : Counted(relationships, "relationship", "relationships")
                    + (relatedProcesses.Count < processes
                        ? string.Create(CultureInfo.CurrentCulture, $" among {relatedProcesses.Count:N0} of them")
                        : string.Empty));
            return graphDisplay.Nodes.Any(node => node.Kind is GraphNodeKind.Group or GraphNodeKind.OtherMembers
                    or GraphNodeKind.Remainder)
                ? text + " · drawn as " + Counted(graphDisplay.Nodes.Count, "node", "nodes")
                : text;
        }
    }

    private static string Counted(int count, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? one : many)}");

    /// <summary>Stable coordinates of every drawn node, by its key.</summary>
    public IReadOnlyDictionary<string, GraphPoint> DisplayPositions => displayPositions;

    /// <summary>Every position this workspace has laid out, copied for a later publication of the same session to keep.</summary>
    public IReadOnlyDictionary<string, GraphPoint> LaidOutPositions =>
        new ReadOnlyDictionary<string, GraphPoint>(new Dictionary<string, GraphPoint>(layoutMemory, StringComparer.Ordinal));

    /// <summary>Completes when this workspace's most recent off-thread layout has applied or reported a problem.</summary>
    public Task LayoutReady { get; private set; }

    public string? GraphLayoutProblem => graphProjectionProblem ?? graphWorkerProblem;

    /// <summary>
    /// The drawn node keyboard focus follows: the selected aggregate, the selected group's collapsed node or first drawn
    /// node, or the node the selected process is drawn as or inside.
    /// </summary>
    public string? SelectedGraphNodeKey
    {
        get
        {
            if (selectedClusterKey is { } cluster)
            {
                return graphDisplay.Node(cluster)?.Key;
            }

            if (selectedGroupKey is not null)
            {
                (IReadOnlySet<string> whole, IReadOnlySet<string> part) = GraphSelection();
                return whole.Order(StringComparer.Ordinal).FirstOrDefault() ?? part.Order(StringComparer.Ordinal).FirstOrDefault();
            }

            return selectedProcess is { } process ? graphDisplay.NodeOf(process.Id)?.Key : null;
        }
    }

    /// <summary>Drawn nodes that stand for nothing but the selection: selected rings in the graph.</summary>
    public IReadOnlySet<string> SelectedGraphNodeKeys => GraphSelection().Whole;

    /// <summary>
    /// Aggregate nodes that hold part of the selection among other processes, such as a selected quiet process inside
    /// No relationships. The graph marks them apart from a whole selection, so an aggregate is never passed off as the
    /// selected process or group.
    /// </summary>
    public IReadOnlySet<string> PartlySelectedGraphNodeKeys => GraphSelection().Part;

    /// <summary>The drawn aggregate the user selected when it is not one executable group; null otherwise.</summary>
    public GraphDisplayNode? SelectedCluster => selectedClusterKey is { } key ? graphDisplay.Node(key) : null;

    /// <summary>The executable group selected by its row or its collapsed node; null otherwise.</summary>
    public ProcessGroup? SelectedGroup => selectedGroupKey is { } key
        ? wholeSnapshot.Groups.FirstOrDefault(group => group.Key == key)
        : null;

    /// <summary>
    /// The drawn nodes the selection covers wholly and in part. The graph reads them on every repaint, so they are worked
    /// out once for each drawing, snapshot and selection and then reused (R11).
    /// </summary>
    private (IReadOnlySet<string> Whole, IReadOnlySet<string> Part) GraphSelection()
    {
        if (graphSelection is { } known && ReferenceEquals(known.Display, graphDisplay)
            && ReferenceEquals(known.Snapshot, wholeSnapshot) && known.Cluster == selectedClusterKey
            && known.Group == selectedGroupKey && ReferenceEquals(known.Process, selectedProcess))
        {
            return (known.Whole, known.Part);
        }

        (IReadOnlySet<string> Whole, IReadOnlySet<string> Part) found = ComputeGraphSelection();
        graphSelection = (graphDisplay, wholeSnapshot, selectedClusterKey, selectedGroupKey, selectedProcess, found.Whole, found.Part);
        return found;
    }

    private (GraphDisplay Display, WorkspaceSnapshot Snapshot, string? Cluster, string? Group, ProcessNode? Process,
        IReadOnlySet<string> Whole, IReadOnlySet<string> Part)? graphSelection;

    private (IReadOnlySet<string> Whole, IReadOnlySet<string> Part) ComputeGraphSelection()
    {
        var whole = new HashSet<string>(StringComparer.Ordinal);
        var part = new HashSet<string>(StringComparer.Ordinal);
        if (selectedClusterKey is { } cluster)
        {
            if (graphDisplay.Node(cluster) is not null) whole.Add(cluster);
            return (whole, part);
        }

        HashSet<ProcessInstanceId> members = chosenProcesses.Count > 0 ? [.. chosenProcesses]
            : selectedGroupKey is { } group
            ? [.. wholeSnapshot.Processes.Where(process => process.GroupKey == group).Select(process => process.Id)]
            : selectedProcess is { } process ? [process.Id] : [];
        foreach (GraphDisplayNode node in members
            .Select(member => graphDisplay.NodeOf(member))
            .OfType<GraphDisplayNode>()
            .DistinctBy(node => node.Key))
        {
            (node.Members.All(members.Contains) ? whole : part).Add(node.Key);
        }

        return (whole, part);
    }

    /// <summary>
    /// Selects a drawn node. A process node selects that process everywhere; a collapsed group selects that group and, at
    /// the machine rung, its row, so Enter opens it; another aggregate is selected in the graph, and the inspector says
    /// what it holds and where each member is listed.
    /// </summary>
    public void SelectGraphNode(string key)
    {
        if (graphDisplay.Node(key) is not { } node)
        {
            return;
        }

        if (node.Process is { } process)
        {
            SelectProcess(process);
            return;
        }

        if (node is { Kind: GraphNodeKind.Group, GroupKey: { } group })
        {
            SelectGroup(group, syncRow: true);
            return;
        }

        SelectedProcess = null;
        selectedGroupKey = null;
        selectedClusterKey = key;
        chosenChannelKey = null;
        chosenProcesses.Clear();
        graphSelection = null;
        describesRow = false;
        ForgetRelationship();
        if (ladder.Current.Level == DetailLevel.Machine && selectedRung is not null)
        {
            // A machine-rung row is a group; an aggregate spans groups, so no row stands for it.
            selectedRung = null;
            OnPropertyChanged(nameof(SelectedRung));
        }

        RaiseGraphSelectionChanged();
    }

    /// <summary>Selects an executable group: from its ranked row, or from its collapsed node, which also selects the row.</summary>
    private void SelectGroup(string groupKey, bool syncRow)
    {
        SelectedProcess = null;
        selectedClusterKey = null;
        selectedGroupKey = groupKey;
        chosenChannelKey = null;
        chosenProcesses.Clear();
        graphSelection = null;
        describesRow = false;
        ForgetRelationship();
        if (syncRow && ladder.Current.Level == DetailLevel.Machine
            && RungRows.FirstOrDefault(row => row.Key == groupKey) is { } row)
        {
            selectedRung = row;
            OnPropertyChanged(nameof(SelectedRung));
        }

        RaiseGraphSelectionChanged();
    }

    /// <summary>
    /// §6.7's double-click on an edge: opens the one relationship it stands for, as <see cref="OpenRelationship"/> does. An
    /// aggregate edge stands for several relationships and opens nothing.
    /// </summary>
    public bool OpenGraphEdge(string edgeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeKey);
        return graphDisplay.Edges.FirstOrDefault(edge => edge.Key == edgeKey) is { Relationships.Count: 1 } drawn
            && OpenRelationship(drawn.Relationships[0]);
    }

    /// <summary>
    /// Opens one relationship, from its edge or from its row in the relationship table, the graph's keyboard and
    /// screen-reader equivalent (R15). The ladder descends one rung at a time from the machine, through the source
    /// process's group, to the process. A relationship resting on exactly one channel goes on to that channel. An RPC
    /// relationship goes on to the calls its links join, which the process's evidence step reads by the relationship's own
    /// key (R4). Any other stops at the process, whose rows list its channels. The breadcrumb states every rung, and each
    /// Esc climbs one.
    /// </summary>
    public bool OpenRelationship(string relationshipKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipKey);
        if (wholeSnapshot.Edges.FirstOrDefault(edge => edge.Key == relationshipKey) is not { } relationship
            || wholeSnapshot.Processes.FirstOrDefault(process => process.Id == relationship.SourceId) is not { } source)
        {
            return false;
        }

        string[] process = [source.GroupKey, source.Id.ToString()];
        if (relationship.Rule != RelationRule.RpcCallPeer)
        {
            Channel[] channels = [.. Snapshot.Channels.Where(channel => channel.EdgeKey == relationship.Key)];
            return DescendAlong(channels.Length == 1 ? [.. process, channels[0].Key] : process, selectedInterval);
        }

        // Linked calls are records only the session holds, so there is nothing to open without it.
        if (evidenceSource is null || !TryBuildDescents(process, Snapshot, out List<LadderDescent> descents))
        {
            return false;
        }

        ladder.RecordInterval(selectedInterval);
        if (!ladder.TryReturnTo(0, out _)) return false;
        foreach (LadderDescent descent in descents) _ = TryDescend(descent);
        string peer = wholeSnapshot.Processes.FirstOrDefault(node => node.Id == relationship.TargetId)?.NameWithPid
            ?? "an unknown process";
        _ = TryDescend(LadderProjection.EvidenceDescentFor(ladder.Current, selectedInterval ?? ladder.Current.Viewport,
            new(DetailLevel.Channel, relationship.Key, LinkedCalls(source.NameWithPid, peer)),
            "Evidence was reached from an RPC relationship: the calls its links join."));
        AfterNavigation();
        return true;
    }

    /// <summary>Enter, or a double click, on the relationship chosen in the table: <see cref="OpenRelationship"/>.</summary>
    public bool OpenSelectedRelationship() => selectedRelationship is { } row && OpenRelationship(row.Key);

    /// <summary>
    /// Opens the records of the call at the other end of an RPC call row (`contracts/operations-v1.md` §5c): the ladder
    /// runs from the machine through that call's process group, its process and its own channel, which is read by its key
    /// as a restored rung is, to the call's records, which its key finds wherever the channel's pages would list it. The
    /// breadcrumb states every rung, and each Esc climbs one. False when the row links no call, or the process is gone.
    /// </summary>
    public bool OpenOtherEnd(RungRow? row = null)
    {
        row ??= selectedRung;
        if (row is not { OtherEndKey: { } callKey }
            || !RpcChannelKeys.TryParseCall(callKey, out string channelKey, out _)
            || !RpcChannelKeys.TryParseChannel(channelKey, out ProcessInstanceId instance, out RpcCallSide side, out Guid? rpcInterface)
            || wholeSnapshot.Processes.FirstOrDefault(process => process.Id == instance) is not { } process
            || !TryBuildDescents([process.GroupKey, process.Id.ToString()], Snapshot, out List<LadderDescent> descents))
        {
            return false;
        }

        ladder.RecordInterval(selectedInterval);
        if (!ladder.TryReturnTo(0, out _)) return false;
        foreach (LadderDescent descent in descents) _ = TryDescend(descent);
        string channelName = (side == RpcCallSide.Client ? "RPC calls to " : "RPC calls served on ") + RpcInterfaceNames.Describe(rpcInterface);
        _ = TryDescend(LadderProjection.DescentFor(RpcChannelRow(channelKey, channelName, string.Empty, 0), ladder.Current,
            selectedInterval ?? ladder.Current.Viewport));
        var call = new LadderRow(callKey, row.OtherEndLabel ?? "RPC call", string.Empty, 2, null, Mechanism.Rpc,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
        _ = TryDescend(LadderProjection.DescentFor(call, ladder.Current, selectedInterval ?? ladder.Current.Viewport));
        AfterNavigation();
        return true;
    }

    /// <summary>
    /// Goes to an entity by the rows a person would choose. Validate the whole path before mutating the ladder so a
    /// stale hit cannot send someone back to the machine rung and leave them there. The rung left keeps
    /// <paramref name="leftWith"/>, the interval the user had there before the jump began.
    /// </summary>
    private bool DescendAlong(IReadOnlyList<string> path, TimeRange? leftWith)
    {
        if (!TryBuildDescents(path, Snapshot, out List<LadderDescent> descents)) return false;
        ladder.RecordInterval(leftWith);
        if (!ladder.TryReturnTo(0, out _)) return false;
        foreach (LadderDescent descent in descents) _ = TryDescend(descent);
        AfterNavigation();
        return true;
    }

    private bool TryBuildDescents(IReadOnlyList<string> path, WorkspaceSnapshot snapshot,
        out List<LadderDescent> descents)
    {
        descents = new(path.Count);
        if (path.Count == 0) return false;
        var preview = new DetailLadder(ladder.Breadcrumb[0]);
        foreach (string key in path)
        {
            LadderRow? row = LadderProjection.Project(snapshot, preview.Current).Rows.FirstOrDefault(candidate => candidate.Key == key);
            if (row is null)
            {
                return false;
            }
            LadderDescent descent = LadderProjection.DescentFor(row, preview.Current,
                selectedInterval ?? preview.Current.Viewport);
            if (!preview.TryDescend(descent, out _)) return false;
            descents.Add(descent);
        }
        return true;
    }

    private string searchText = string.Empty;
    private SearchResult searchResult = new(string.Empty, [], 0);
    private IReadOnlyList<SearchRow> searchRows = [];
    private SearchRow? selectedSearchResult;

    /// <summary>
    /// §6.7's search (Ctrl+F): names, PIDs and channel endpoints of the published session, never payloads. While it has
    /// text the rail lists its hits instead of the rung's ranked table; opening a hit goes there and clears it.
    /// </summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            value ??= string.Empty;
            if (searchText == value)
            {
                return;
            }

            searchText = value;
            searchResult = WorkspaceSearch.Find(wholeSnapshot, value);
            searchRows = [.. searchResult.Hits.Select(SearchRow.Of)];
            selectedSearchResult = searchRows.Count > 0 ? searchRows[0] : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearching));
            OnPropertyChanged(nameof(SearchResults));
            OnPropertyChanged(nameof(SelectedSearchResult));
            OnPropertyChanged(nameof(SearchSummary));
            OnPropertyChanged(nameof(ShowsRankedTable));
            OnPropertyChanged(nameof(ShowsEmptyReason));
            OnPropertyChanged(nameof(ShowsLoadMoreEvidence));
            OnPropertyChanged(nameof(ShowsLoadMore));
        }
    }

    public bool IsSearching => searchResult.Query.Length > 0;

    public IReadOnlyList<SearchRow> SearchResults => searchRows;

    public SearchRow? SelectedSearchResult
    {
        get => selectedSearchResult;
        set
        {
            if (ReferenceEquals(selectedSearchResult, value)) return;
            selectedSearchResult = value;
            OnPropertyChanged();
        }
    }

    /// <summary>How many entities matched, and what to do next; bounded results say how many more there are.</summary>
    public string SearchSummary => !IsSearching
        ? string.Empty
        : searchResult.Matched == 0
            ? "No matches · names, PIDs and channel endpoints are searched"
            : searchResult.Matched > searchResult.Hits.Count
                ? string.Create(CultureInfo.CurrentCulture,
                    $"{searchResult.Hits.Count:N0} of {searchResult.Matched:N0} matches · refine the search for the rest")
                : Counted(searchResult.Matched, "match", "matches") + " · Enter opens the selected one, Esc clears";

    /// <summary>Whether the rail shows the rung's ranked table: not while a search lists its hits there.</summary>
    public bool ShowsRankedTable => !IsEmptyRung && !IsSearching;

    /// <summary>
    /// Whether the rail explains an empty rung: not while a search lists its hits there, and in a workspace with no session
    /// only while a capture is on its way to its first view, since otherwise the Explore card says what to do (§3.1).
    /// </summary>
    public bool ShowsEmptyReason => IsEmptyRung && !IsSearching && (!emptyWorkspace || awaitingCaptureNote is not null);

    /// <summary>The empty rung's heading: a rung with nothing at it, or a workspace whose capture has published nothing yet.</summary>
    public string EmptyHeading => emptyWorkspace ? "NOTHING RECORDED YET" : "NOTHING AT THIS LEVEL";

    public bool ShowsLoadMoreEvidence => CanLoadMoreEvidence && !IsSearching;

    /// <summary>Whether the rung has another page to load: source records at the evidence rung, calls at an RPC channel's.</summary>
    public bool CanLoadMore => CanLoadMoreEvidence || CanLoadMoreCalls || CanLoadMoreExchanges;

    public bool ShowsLoadMore => CanLoadMore && !IsSearching;

    public string LoadMoreLabel => CanLoadMoreCalls ? "Load more calls (M)"
        : CanLoadMoreExchanges ? "Load more exchanges (M)"
        : "Load more records (M)";

    public string LoadMoreName => CanLoadMoreCalls ? "Load the next page of calls"
        : CanLoadMoreExchanges ? "Load the next page of HTTP exchanges"
        : "Load the next page of source records";

    /// <summary>Loads the rung's next page, whichever it lists.</summary>
    public Task LoadMoreAsync() => CanLoadMoreCalls ? LoadMoreCallsAsync()
        : CanLoadMoreExchanges ? LoadMoreExchangesAsync()
        : LoadMoreEvidenceAsync();

    /// <summary>Goes to a search hit - the selected one when none is named - and clears the search.</summary>
    public bool OpenSearchResult(SearchRow? row = null)
    {
        row ??= selectedSearchResult;
        if (row is null)
        {
            return false;
        }

        // Search covers the whole published session, not only an analysis brush. Clear that brush before opening a
        // match outside it so the hit is not silently missing from the ranked rung. The rung left keeps it.
        TimeRange? leftWith = selectedInterval;
        if (selectedInterval is not null)
        {
            if (!TryBuildDescents(row.Hit.Path, wholeSnapshot, out _)) return false;
            selection.Clear();
        }
        bool opened = DescendAlong(row.Hit.Path, leftWith);
        if (opened) SearchText = string.Empty;
        return opened;
    }

    /// <summary>Opens a collapsed executable group only where the ladder can actually descend into that group.</summary>
    public bool OpenGraphGroup(string key)
    {
        if (ladder.Current.Level != DetailLevel.Machine
            || graphDisplay.Node(key) is not { Kind: GraphNodeKind.Group })
        {
            return false;
        }

        SelectGraphNode(key);
        return Descend();
    }

    /// <summary>
    /// Opens a drawn node as Enter on its row would (§6.3, §6.7): a group at the machine rung, and a process at any rung,
    /// through the ladder path a search hit takes - its group, then itself - so the breadcrumb states every rung and Esc
    /// climbs each. A peer drawn beside the rung's own process opens its own rung this way. A node standing for many
    /// processes, and the rung's own process, open nothing.
    /// </summary>
    public bool OpenGraphNode(string key)
    {
        if (OpenGraphGroup(key)) return true;
        return graphDisplay.Node(key) is { } node && OpensProcess(node)
            && DescendAlong([node.GroupKey!, node.Process!.Value.ToString()], selectedInterval);
    }

    /// <summary>Whether Enter on a drawn node opens its process: a process node other than the rung's own process.</summary>
    private bool OpensProcess(GraphDisplayNode node) =>
        node is { Kind: GraphNodeKind.Process, Process: { } process, GroupKey: not null }
        && !(ladder.Current.Level == DetailLevel.ProcessInstance
            && string.Equals(ladder.Current.Focus?.Key, process.ToString(), StringComparison.Ordinal));

    private void RaiseGraphSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedGraphNodeKey));
        OnPropertyChanged(nameof(SelectedGraphNodeKeys));
        OnPropertyChanged(nameof(PartlySelectedGraphNodeKeys));
        OnPropertyChanged(nameof(SelectedCluster));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(DescribedRow));
        OnPropertyChanged(nameof(SelectionActions));
        OnPropertyChanged(nameof(HasSelectionActions));
        OnPropertyChanged(nameof(EvidenceHeading));
        OnPropertyChanged(nameof(EvidenceSummary));
        OnPropertyChanged(nameof(PinActionLabel));
        OnPropertyChanged(nameof(CanTogglePin));
        RaiseLineageChanged();
        UpdateHighlight();
        FollowDescribedBytes();
    }

    private ReadOnlyDictionary<ProcessInstanceId, GraphPoint> ProcessPositions() =>
        new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(wholeDisplay.Nodes
            .Where(node => displayPositions.ContainsKey(node.Key))
            .SelectMany(node => node.Members.Select(member => (member, point: displayPositions[node.Key])))
            .ToDictionary(entry => entry.member, entry => entry.point));

    /// <summary>
    /// What a rung asks the graph to draw (§3.2): a stable key for the drawing, the opened group, the kept process, and the
    /// neighbourhood drawn - null at the machine rung, which draws everything.
    /// </summary>
    private sealed record GraphFocus(
        string Key,
        string? Expanded,
        ProcessInstanceId? Kept,
        IReadOnlySet<ProcessInstanceId>? Neighborhood,
        string Description);

    private static readonly GraphFocus MachineFocus = new("machine", null, null, null, "Whole machine");

    private GraphFocus graphFocus = MachineFocus;

    /// <summary>
    /// The graph each rung draws (§3.2): the machine rung everything; a group its members and their peers; a process
    /// instance itself and its peers one hop out; a channel or operation the channel's two participants. Everything else is
    /// counted in one context node. The evidence rung keeps the graph of the rung it was reached from.
    /// </summary>
    private GraphFocus FocusOfLadder()
    {
        int last = ladder.Breadcrumb.Count - 1;
        while (last > 0 && ladder.Breadcrumb[last].Level == DetailLevel.Evidence)
        {
            last--;
        }

        NavigationState[] path = [.. ladder.Breadcrumb.Take(last + 1)];
        string? expanded = path.LastOrDefault(rung => rung.Level == DetailLevel.Group)?.Focus?.Key;
        ProcessNode? kept = path.LastOrDefault(rung => rung.Level == DetailLevel.ProcessInstance)?.Focus?.Key is { } focus
            ? wholeSnapshot.Processes.FirstOrDefault(process => string.Equals(process.Id.ToString(), focus, StringComparison.Ordinal))
            : null;
        if (expanded is not null && wholeSnapshot.Groups.All(group => group.Key != expanded))
        {
            expanded = null;
        }

        switch (path[^1].Level)
        {
            case DetailLevel.Group when expanded is not null:
            {
                string name = wholeSnapshot.Groups.First(group => group.Key == expanded).Name;
                ProcessInstanceId[] members = [.. wholeSnapshot.Processes
                    .Where(process => process.GroupKey == expanded)
                    .Select(process => process.Id)];
                HashSet<ProcessInstanceId> neighborhood = WithPeers(members);
                return new($"group:{expanded}", expanded, null, neighborhood,
                    AndItsPeers(name, neighborhood.Count > members.Length));
            }

            case DetailLevel.ProcessInstance when kept is not null:
                return ProcessFocus(expanded, kept);

            case DetailLevel.Channel or DetailLevel.Operation:
            {
                string? channelKey = path.LastOrDefault(rung => rung.Level == DetailLevel.Channel)?.Focus?.Key;
                Channel? channel = wholeSnapshot.Channels.FirstOrDefault(candidate => candidate.Key == channelKey);
                CommunicationEdge? relationship = channel is null
                    ? null
                    : wholeSnapshot.Edges.FirstOrDefault(edge => edge.Key == channel.EdgeKey);
                if (channel is not null && relationship is not null)
                {
                    HashSet<ProcessInstanceId> participants = [relationship.SourceId, relationship.TargetId];
                    return new($"channel:{channel.Key}", expanded,
                        kept is not null && participants.Contains(kept.Id) ? kept.Id : null,
                        participants, $"Channel {channel.Name}");
                }

                // A channel this generation no longer projects keeps the process it was reached from in focus.
                return kept is not null ? ProcessFocus(expanded, kept) : MachineFocus;
            }

            default:
                return MachineFocus;
        }
    }

    private GraphFocus ProcessFocus(string? expanded, ProcessNode process)
    {
        HashSet<ProcessInstanceId> neighborhood = WithPeers([process.Id]);
        return new(
            $"process:{process.Id}",
            expanded is not null && process.GroupKey == expanded ? expanded : null,
            process.Id,
            neighborhood,
            AndItsPeers(process.NameWithPid, neighborhood.Count > 1));
    }

    /// <summary>A focus's name, which claims peers only when some process outside the focus is one relationship away.</summary>
    private static string AndItsPeers(string focus, bool hasPeers) => hasPeers ? $"{focus} and its peers" : focus;

    /// <summary>The given processes and every process one relationship away from them, in the whole published scope.</summary>
    private HashSet<ProcessInstanceId> WithPeers(IEnumerable<ProcessInstanceId> seeds)
    {
        HashSet<ProcessInstanceId> neighborhood = [.. seeds];
        HashSet<ProcessInstanceId> origin = [.. neighborhood];
        foreach (CommunicationEdge edge in wholeSnapshot.Edges)
        {
            if (origin.Contains(edge.SourceId)) neighborhood.Add(edge.TargetId);
            if (origin.Contains(edge.TargetId)) neighborhood.Add(edge.SourceId);
        }

        return neighborhood;
    }

    /// <summary>
    /// The graph the ladder's focus asks for (<see cref="FocusOfLadder"/>). A graph with the same nodes keeps its layout;
    /// one that changed is laid out again from where the nodes it shares already are.
    /// </summary>
    private void RefreshGraphDisplay()
    {
        GraphFocus focus = FocusOfLadder();
        if (focus.Key == graphFocus.Key)
        {
            return;
        }

        graphFocus = focus;
        string? expanded = focus.Expanded;
        ProcessInstanceId? kept = focus.Kept;
        GraphDisplay previous = wholeDisplay;
        GraphDisplay next = ProjectGraph(expanded, kept, focus.Neighborhood);
        bool sameNodes = next.Nodes.Select(node => node.Key).SequenceEqual(previous.Nodes.Select(node => node.Key))
            && next.Edges.Select(edge => edge.Key).SequenceEqual(previous.Edges.Select(edge => edge.Key));
        wholeDisplay = next;
        graphDisplay = scopedSnapshot is null ? wholeDisplay : ScopeGraph(wholeDisplay, Snapshot);
        if (selectedClusterKey is not null && graphDisplay.Node(selectedClusterKey) is null)
        {
            selectedClusterKey = null;
        }

        if (!sameNodes)
        {
            // A node drawn before keeps its place: one still on screen, or one laid out earlier in this workspace, such as
            // the machine rung's nodes on the way back up. Until the new layout arrives, a node new to this drawing is
            // drawn where its members were drawn - an opened group's members where its cluster was - and is left out of
            // the layout's starting positions, so the layout seeds it in its band instead of stacking it on its siblings.
            var positions = new Dictionary<string, GraphPoint>(StringComparer.Ordinal);
            provisionalKeys.Clear();
            foreach (GraphDisplayNode node in next.Nodes)
            {
                if (displayPositions.TryGetValue(node.Key, out GraphPoint existing)
                    || layoutMemory.TryGetValue(node.Key, out existing))
                {
                    positions[node.Key] = existing;
                    continue;
                }

                GraphPoint[] origins = [.. node.Members
                    .Select(member => previous.NodeOf(member)?.Key)
                    .Where(key => key is not null && displayPositions.ContainsKey(key))
                    .Select(key => displayPositions[key!])];
                positions[node.Key] = origins.Length == 0
                    ? new(0.5, 0.5)
                    : new(origins.Average(point => point.X), origins.Average(point => point.Y));
                provisionalKeys.Add(node.Key);
            }

            displayPositions = new ReadOnlyDictionary<string, GraphPoint>(positions);
            GraphPositions = ProcessPositions();
            OnPropertyChanged(nameof(DisplayPositions));
            OnPropertyChanged(nameof(GraphPositions));
        }

        OnPropertyChanged(nameof(GraphDisplay));
        OnPropertyChanged(nameof(GraphSummary));
        RaiseGraphSelectionChanged();
        if (!sameNodes)
        {
            LayoutReady = BuildLayoutAsync(focus.Neighborhood is null ? graphIdentity : $"{graphIdentity}|{focus.Key}", next);
        }
    }

    /// <summary>Nodes drawn at a provisional position until the layout for the current drawing arrives.</summary>
    private readonly HashSet<string> provisionalKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The last laid-out position of every node this workspace has drawn, and of those an earlier publication carried in.
    /// Keys name roles that persist (a process instance, a group, an aggregate), so a node that returns returns to its place.
    /// </summary>
    private readonly Dictionary<string, GraphPoint> layoutMemory = new(StringComparer.Ordinal);

    /// <summary>
    /// Nodes the user placed by hand, by the same stable keys, in graph coordinates. They are hard constraints (§19.4)
    /// and are kept for as long as the session is open, across refreshes and rungs. The main window keeps them in the
    /// investigation a session was opened from (§26.3); a session opened on its own keeps them only while it is open.
    /// </summary>
    private readonly Dictionary<string, GraphPoint> graphPins = new(StringComparer.Ordinal);

    /// <summary>The identity the current drawing is laid out under: the graph's, qualified by the ladder's focus.</summary>
    private string layoutIdentity = string.Empty;

    /// <summary>Every pin this workspace holds, copied for a later publication of the same session to keep.</summary>
    public IReadOnlyDictionary<string, GraphPoint> GraphPins =>
        new ReadOnlyDictionary<string, GraphPoint>(new Dictionary<string, GraphPoint>(graphPins, StringComparer.Ordinal));

    /// <summary>The drawn nodes the user pinned.</summary>
    public IReadOnlySet<string> PinnedGraphNodeKeys =>
        graphPins.Keys.Where(key => graphDisplay.Node(key) is not null).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when this stable graph key has a user placement constraint.</summary>
    public bool IsGraphNodePinned(string key) => graphPins.ContainsKey(key);

    /// <summary>The inspector's pin action for the selected node, in words, with its key.</summary>
    public string PinActionLabel => SelectedGraphNodeKey is { } key && IsGraphNodePinned(key) ? "Unpin node (P)" : "Pin node (P)";

    public bool CanTogglePin => SelectedGraphNodeKey is { } key && graphDisplay.Node(key) is not null;

    /// <summary>
    /// Pins a drawn node where the user put it. The drawing shows it there at once; the layout keeps it there and settles
    /// the rest around it, anchored as for any refresh.
    /// </summary>
    public bool PinGraphNode(string key, GraphPoint at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (wholeDisplay.Node(key) is null || !at.IsValid)
        {
            return false;
        }

        graphPins[key] = at;
        layoutMemory[key] = at;
        provisionalKeys.Remove(key);
        displayPositions = new ReadOnlyDictionary<string, GraphPoint>(
            new Dictionary<string, GraphPoint>(displayPositions, StringComparer.Ordinal) { [key] = at });
        AfterPinsChanged();
        return true;
    }

    /// <summary>Releases a pin; the node stays where it is until the layout moves it.</summary>
    public bool UnpinGraphNode(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!graphPins.Remove(key))
        {
            return false;
        }

        AfterPinsChanged();
        return true;
    }

    /// <summary>P: pins the selected node where it is drawn, or releases its pin.</summary>
    public bool TogglePinSelectedGraphNode() =>
        SelectedGraphNodeKey is { } key && graphDisplay.Node(key) is not null
        && (graphPins.ContainsKey(key)
            ? UnpinGraphNode(key)
            : displayPositions.TryGetValue(key, out GraphPoint at) && PinGraphNode(key, at));

    /// <summary>
    /// L: lays the drawn graph out afresh, as when it was first drawn, forgetting where its nodes were remembered. Pins
    /// stay: they are the user's placements, not the layout's.
    /// </summary>
    public bool RelayoutGraph()
    {
        if (wholeDisplay.Nodes.Count == 0)
        {
            return false;
        }

        foreach (GraphDisplayNode node in wholeDisplay.Nodes)
        {
            if (!graphPins.ContainsKey(node.Key))
            {
                layoutMemory.Remove(node.Key);
                provisionalKeys.Add(node.Key);
            }
        }

        LayoutReady = BuildLayoutAsync(layoutIdentity, wholeDisplay);
        return true;
    }

    private void AfterPinsChanged()
    {
        GraphPositions = ProcessPositions();
        OnPropertyChanged(nameof(DisplayPositions));
        OnPropertyChanged(nameof(GraphPositions));
        OnPropertyChanged(nameof(PinnedGraphNodeKeys));
        OnPropertyChanged(nameof(PinActionLabel));
        LayoutReady = BuildLayoutAsync(layoutIdentity, wholeDisplay);
    }

    /// <summary>
    /// Whether any interval's coverage is limited. The summary then reads in the caution ink; complete coverage, or none
    /// to report, is a plain statement and must not look like a warning (§6.6). A generation that publishes no coverage
    /// ledger, as a capture's do until it stops, has judged nothing yet: that is said plainly, not counted as limits.
    /// </summary>
    public bool CoverageLimited => Snapshot.CoverageLedgerPublished
        && Snapshot.Timeline.Any(bucket => bucket.Coverage != CoverageState.Covered);

    /// <summary>Coverage is derived from the snapshot's intervals, never from a prototype constant.</summary>
    public string CoverageSummary
    {
        get
        {
            if (Snapshot.Timeline.Count == 0)
            {
                return "Coverage not quantified";
            }

            if (!Snapshot.CoverageLedgerPublished)
            {
                return "Coverage unknown until a coverage ledger is published";
            }

            var limitations = new List<string>();
            int gaps = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.PartialGap);
            if (gaps > 0)
            {
                limitations.Add(Intervals(gaps, "partial-gap"));
            }

            int unknown = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.UnknownCoverage);
            if (unknown > 0)
            {
                limitations.Add(Intervals(unknown, "coverage-unknown"));
            }

            int omitted = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.NotCollected);
            if (omitted > 0)
            {
                limitations.Add(Intervals(omitted, "not-collected"));
            }

            int reduced = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.ReducedFidelity);
            if (reduced > 0)
            {
                limitations.Add(Intervals(reduced, "reduced-fidelity"));
            }

            return limitations.Count == 0
                ? $"Coverage reported for {Intervals(Snapshot.Timeline.Count, null)}"
                : string.Join(" · ", limitations) + " · not extrapolated";

            static string Intervals(int count, string? kind) => string.Create(CultureInfo.CurrentCulture,
                $"{count:N0} {(kind is null ? string.Empty : kind + " ")}{(count == 1 ? "interval" : "intervals")}");
        }
    }

    private async Task BuildLayoutAsync(string identity, GraphDisplay display)
    {
        layoutIdentity = identity;
        try
        {
            // A node that existed before keeps its place, a node new to this drawing is seeded beside its neighbours, and
            // a node the user pinned stays exactly where it was put (§19.4 hard constraints).
            GraphDisplayLayout? result = await graphLayout.RequestDisplayAsync(identity, display,
                previous: displayPositions
                    .Where(entry => display.Node(entry.Key) is not null && !provisionalKeys.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                pins: graphPins
                    .Where(entry => display.Node(entry.Key) is not null)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
            if (disposed || result is null || result.GraphIdentity != identity || !ReferenceEquals(display, wholeDisplay))
            {
                return;
            }

            provisionalKeys.Clear();
            displayPositions = result.Positions;
            foreach ((string key, GraphPoint point) in result.Positions)
            {
                layoutMemory[key] = point;
            }

            GraphPositions = ProcessPositions();
            SetGraphWorkerProblem(null);
            OnPropertyChanged(nameof(DisplayPositions));
            OnPropertyChanged(nameof(GraphPositions));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            SetGraphWorkerProblem(exception.Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelEvidence();
        CancelRpc();
        CancelHttp();
        CancelConnections();
        intervalQuery?.Cancel();
        intervalQuery?.Dispose();
        intervalQuery = null;
        byteReads.Cancel();
        callReads.Cancel();
        peerReads.Cancel();
        selectionBytes.Cancel();
        CancelIntervalBytes();
        CancelTimelineBytes();
        timelineQuery?.Cancel();
        timelineQuery?.Dispose();
        timelineQuery = null;
        CancelHighlight();
        graphLayout.Dispose();
    }

    /// <summary>Mechanism legend with glyphs, the redundant channel beside hue (R14).</summary>
    public IReadOnlyList<LegendEntry> Legend { get; private set; }

    /// <summary>Rebuilds what carries a colour after the theme's mode changed: the legend's hues and inks (§6.1, §6.6).</summary>
    public void RefreshTheme()
    {
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeResources.CurrentMode);
        OnPropertyChanged(nameof(Legend));
    }

    /// <summary>Table equivalent of the graph. It yields the same relationships the canvas draws (R15).</summary>
    public IReadOnlyList<RelationshipRow> Relationships => relationships;

    /// <summary>
    /// The relationships the table lists: those the graph draws (R15). At the machine rung that is every one; below it,
    /// every one with an end in the rung's neighbourhood, which the graph draws between its processes or out to Rest of the
    /// machine. The others are counted in <see cref="RelationshipTableScope"/>, never dropped without a word.
    /// </summary>
    private WorkspaceSnapshot ListedRelationships() => graphFocus.Neighborhood is not { } drawn
        ? Snapshot
        : Snapshot with { Edges = [.. Snapshot.Edges.Where(edge => drawn.Contains(edge.SourceId) || drawn.Contains(edge.TargetId))] };

    /// <summary>What the relationship table lists, as the graph's caption names it, and how many relationships it leaves out.</summary>
    public string RelationshipTableScope
    {
        get
        {
            string listed = Counted(relationships.Count, "relationship", "relationships");
            int elsewhere = Snapshot.Edges.Count - relationships.Count;
            return graphFocus.Neighborhood is null
                ? $"Whole machine · {listed}"
                : $"{graphFocus.Description} · {listed}" + (elsewhere > 0
                    ? string.Create(CultureInfo.CurrentCulture, $" · {elsewhere:N0} more elsewhere, listed at the machine rung")
                    : string.Empty);
        }
    }

    /// <summary>Table equivalent of the timeline, with the same counts and coverage states (R15).</summary>
    /// <summary>
    /// The table equivalent of the timeline (R15): the buckets it draws, which are the zoomed viewport's own once they
    /// have arrived and the whole session's otherwise.
    /// </summary>
    public IReadOnlyList<IntervalRow> Intervals => intervals;

    /// <summary>
    /// Whether the interval table has bytes to state. A real session's timeline counts records per interval and sums no
    /// bytes (`overview-index-v1`), so its rows' bytes are read once the table is shown; a real session with no directory
    /// to read them from leaves the column out rather than calling every interval's bytes unknown.
    /// </summary>
    public bool IntervalTableShowsBytes => !realOverview || ReadsBytes;

    /// <summary>
    /// What the interval table lists, so a zoomed table is never read as the whole session, and on a real session what its
    /// bytes are: each interval's records' own once read, or that they are being read, or why not.
    /// </summary>
    public string IntervalTableScope => !realOverview ? IntervalTableHolds
        : IntervalTableHolds + " · " + IntervalBytesCaption;

    private string IntervalTableHolds
    {
        get
        {
            if (SelectedProcessLane is { } processLane)
            {
                string owner = selectedProcess is { } process ? process.NameWithPid : processLane.ProcessId.ToString();
                return string.Create(CultureInfo.CurrentCulture,
                    $"{owner} owner records · {intervals.Count:N0} exact intervals");
            }
            if (SelectedDirectionBucketLane is { } directionLane)
            {
                return string.Create(CultureInfo.CurrentCulture,
                    $"{selectedDirectionLane.Label} source-direction records · {directionLane.Buckets.Count:N0} exact intervals");
            }

            if (SelectedChannelEndLane is { } endLane)
            {
                return string.Create(CultureInfo.CurrentCulture,
                    $"Records made at {ChannelEndLabel(endLane)} · {endLane.Buckets.Count:N0} exact intervals");
            }

            string lane = ShowsMechanismLanes && SelectedTimelineMechanism is { } mechanism
                ? $" · {EvidenceRowText.MechanismName(mechanism)} lane" : string.Empty;
            return timelineDetail is { } detail
                && (!ShowsMechanismLanes || SelectedTimelineMechanism is null || HasCompleteLaneDetail)
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Zoomed view {WorkspaceTime.FormatRange(detail.Interval, CultureInfo.CurrentCulture)} in {intervals.Count:N0} intervals{lane}")
                : string.Create(CultureInfo.CurrentCulture, $"Whole session in {intervals.Count:N0} intervals{lane}");
        }
    }

    /// <summary>
    /// The ranked table of the rung the user is on. The same gesture works at every rung. At a published session's
    /// evidence rung it lists the admitted source records of the rung's scope, in reading order, as they load.
    /// </summary>
    public IReadOnlyList<RungRow> RungRows => IsEvidenceRung ? evidenceRows
        : IsRpcChannelRung ? rpcCallRows
        : IsHttpChannelRung ? httpExchangeRows
        : rpcChannelRows.Count == 0 && httpChannelRows.Count == 0 && ConnectionCount == 0 ? LadderRows()
        : Ranked([.. LadderRows(), .. ConnectionRows(), .. rpcChannelRows.Select(RankedRpcChannel),
            .. httpChannelRows.Select(RankedHttpChannel)]);

    /// <summary>The ladder's rows as the rail shows them, a process's paired channels named by whom they connect it to.</summary>
    private IReadOnlyList<RungRow> LadderRows()
    {
        IReadOnlyList<RungRow> rows = LadderRowBuilder.Rows(view, ThemeResources.CurrentMode, !ReadsBytes, RateSeconds);
        if (!realOverview || ladder.Current.Level != DetailLevel.ProcessInstance
            || ladder.Current.Focus is not { } focus || !Guid.TryParse(focus.Key, out Guid id))
        {
            return rows;
        }

        var process = new ProcessInstanceId(id);
        Dictionary<string, Channel> channels = Snapshot.Channels.ToDictionary(channel => channel.Key, StringComparer.Ordinal);
        Dictionary<string, CommunicationEdge> edges = Snapshot.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        Dictionary<ProcessInstanceId, string> names = [];
        foreach (ProcessNode node in Snapshot.Processes)
        {
            names.TryAdd(node.Id, node.NameWithPid);
        }

        return [.. rows.Select(row => channels.TryGetValue(row.Key, out Channel? channel)
            && edges.TryGetValue(channel.EdgeKey, out CommunicationEdge? edge)
                ? PeerNamed(row, process, channel, edge, names)
                : row)];
    }

    /// <summary>
    /// A paired channel's row at its process's rung, named by the process at its other end. A channel's own name is its
    /// endpoint pair, and a server's channels all begin with the server's own endpoint, so the rail cut every row before the
    /// peer's port and they read alike. The peer leads, the process's own port and the peer's follow, and the tooltip,
    /// crumb and export keep the channel's own name.
    /// </summary>
    private static RungRow PeerNamed(RungRow row, ProcessInstanceId process, Channel channel, CommunicationEdge edge,
        Dictionary<ProcessInstanceId, string> names)
    {
        ProcessInstanceId peer = edge.SourceId == process ? edge.TargetId : edge.SourceId;
        string peerName = peer == process ? "itself" : names.GetValueOrDefault(peer, "an unknown process");
        string ends = ChannelNames.Compact(channel.Name, swap: channel.FirstHolder is { } first && first != process);
        return row with
        {
            Label = "↔ " + peerName,
            Detail = $"{EvidenceRowText.MechanismName(channel.Mechanism)} · {ends}",
            SpokenLabel = "Channel with " + peerName,
        };
    }

    /// <summary>
    /// A process's TCP and RPC channels ranked together as the ladder ranks rows: by value when a byte ranking gave every
    /// one a value, else by records, then by key (R13).
    /// </summary>
    private static List<RungRow> Ranked(List<RungRow> rows)
    {
        Dictionary<string, RungRow> byKey = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        return [.. LadderProjection.Order(rows.Select(row => row.Source)).Select(source => byKey[source.Key])];
    }

    /// <summary>
    /// An RPC channel row under a byte ranking of its process's channels: RPC carries no size, so the row says so and
    /// ranks after every TCP channel, whose own bytes rank them.
    /// </summary>
    private RungRow RankedRpcChannel(RungRow row) => ShownMeasures is SessionByteMeasures
        && ladder.Current.Level == DetailLevel.ProcessInstance
        && view.Rows.All(channel => channel.Ranked is not null)
            ? row with
            {
                Source = row.Source with { Ranked = new RankedValue(rankBy, null, 0, 0) },
                RankedFigure = "no size",
                RankedSpoken = "RPC carries no size",
            }
            : row;

    /// <summary>
    /// A process's HTTP exchanges under a byte ranking: their bytes are HTTP messages, which never rank with the transport
    /// bytes the channels beside them rank by (P3), so the row says so and ranks after every TCP channel.
    /// </summary>
    private RungRow RankedHttpChannel(RungRow row) => ShownMeasures is SessionByteMeasures
        && ladder.Current.Level == DetailLevel.ProcessInstance
        && view.Rows.All(channel => channel.Ranked is not null)
            ? row with
            {
                Source = row.Source with { Ranked = new RankedValue(rankBy, null, 0, 0) },
                RankedFigure = "HTTP messages",
                RankedSpoken = "its bytes are HTTP messages, which do not rank with transport bytes",
            }
            : row;

    /// <summary>The breadcrumb. It always names the level and the selection at each rung (section 3.2).</summary>
    public IReadOnlyList<CrumbRow> Crumbs => LadderRowBuilder.Crumbs(ladder);

    /// <summary>Filters the descents implied, shown where they can be seen and removed.</summary>
    public IReadOnlyList<FilterRow> Filters => LadderRowBuilder.Filters(ladder.Current);

    public bool HasFilters => ladder.Current.Filters.Count > 0;

    /// <summary>The rung's level as a short badge, so the position is visible without reading the crumbs.</summary>
    public string LevelBadge => string.Create(
        CultureInfo.CurrentCulture,
        $"L{(int)ladder.Current.Level} · {NavigationState.Name(ladder.Current.Level).ToUpperInvariant()}");

    public string LevelSummary => IsEvidenceRung
        ? (evidence?.Scope.Description ?? string.Empty) + " · " + EvidenceStatus
        : IsRpcChannelRung
            ? RpcCallSummary(brief: false)
        : IsHttpChannelRung
            ? HttpExchangeSummary(brief: false)
        : FocusedRealChannel is { } channel
            ? Spoken.Count(channel.ObservationCount, "observed record")
                + $" at this channel's two ends · {FocusedChannelBytes(channel)} · no operation rung; E shows the records"
            : RungTotal(brief: false) + RealScopeNote(brief: false);

    /// <summary>
    /// The rung's total. A process rung that lists only RPC channels totals their call records, which are all its own; with
    /// paired channels beside them the rows overlap, as paired channels' always do, and no single total is stated.
    /// </summary>
    private string RungTotal(bool brief)
    {
        if ((rpcChannelRows.Count == 0 && httpChannelRows.Count == 0 && ConnectionCount == 0) || view.Rows.Count > 0)
        {
            // Under a byte ranking the note beneath the selector states the rows' bytes, so the total leaves out the
            // paired channels' known bytes, which would read as a second, contradicting byte figure.
            // A real session's overview sums no bytes, so its total never states "bytes unknown" for them.
            if (((view.Rows.Count > 0 && view.Rows[0].Ranked is { Metric: RankingMetric.BytesSent or RankingMetric.BytesReceived or RankingMetric.EndpointBytes })
                    || (ReadsBytes && view.KnownBytes is null))
                && view.ObservationCount is { } total)
            {
                return Spoken.Count(total, "observation");
            }

            return brief ? LadderRowBuilder.DescribeTotalShort(view) : LadderRowBuilder.DescribeTotal(view);
        }

        if (httpChannelRows.Count > 0 || ConnectionCount > 0)
        {
            // Transport, call and buffer records are each their own process's, so they add up, but they are not one kind of
            // record, and each kind is counted apart.
            var kinds = new List<string>(3);
            if (ConnectionCount > 0)
            {
                kinds.Add(Spoken.Count(connections!.Connections.Sum(connection => connection.Records), "record") + " on "
                    + Spoken.Count(ConnectionCount, "connection"));
            }

            if (rpcChannelRows.Count > 0) kinds.Add(Spoken.Count(rpcChannelRows.Sum(row => row.Source.ObservationCount), "call record"));
            if (httpChannelRows.Count > 0)
            {
                kinds.Add(Spoken.Count(httpChannelRows.Sum(row => row.Source.ObservationCount), "HTTP buffer record"));
            }

            return string.Join(" and ", kinds);
        }

        long records = rpcChannelRows.Sum(row => row.Source.ObservationCount);
        return brief
            ? Spoken.Count(records, "call record")
            : Spoken.Count(records, "call record") + " in "
                + Spoken.Count(rpcChannelRows.Count, "RPC channel") + " · RPC carries no size";
    }

    /// <summary>The same total in one line, for the narrow ranked-table rail.</summary>
    public string LevelSummaryShort => IsEvidenceRung
        ? EvidenceStatus
        : IsRpcChannelRung
            ? RpcCallSummary(brief: true)
        : IsHttpChannelRung
            ? HttpExchangeSummary(brief: true)
        : FocusedRealChannel is { } channel
            ? Spoken.Count(channel.ObservationCount, "record") + " on this channel · no operation rung"
            : RungTotal(brief: true) + RealScopeNote(brief: true);

    /// <summary>
    /// What a published session's rung total counts, said after it. The machine and group rungs count each process's
    /// own records (`process-activity-v1`), and the machine rung says how many rows no process holds, which only the
    /// timeline counts; the process rung's channels count admitted paired TCP only.
    /// </summary>
    private string RealScopeNote(bool brief)
    {
        if (!realOverview)
        {
            return string.Empty;
        }

        if (ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group)
        {
            return brief ? " · own records"
                : ladder.Current.Level == DetailLevel.Machine && Snapshot.RowsNoProcessHolds is > 0 and { } none
                    ? string.Create(CultureInfo.CurrentCulture,
                        $" · each process's own records; {none:N0} more {(none == 1 ? "row" : "rows")} no process holds, in the timeline only")
                    : " · each process's own records";
        }

        if (ladder.Current.Level == DetailLevel.ProcessInstance && (httpChannelRows.Count > 0 || ConnectionCount > 0))
        {
            // It names what the rung lists, and only what it lists.
            var listed = new List<string>(4) { brief ? "paired TCP" : "admitted paired TCP" };
            if (ConnectionCount > 0) listed.Add(brief ? "connections" : "its connections whose other end no record holds");
            if (rpcChannelRows.Count > 0) listed.Add(brief ? "RPC calls" : "its RPC calls by interface");
            if (httpChannelRows.Count > 0) listed.Add(brief ? "HTTP exchanges" : "its HTTP exchanges");
            string joined = listed.Count == 2 ? $"{listed[0]} and {listed[1]}" : string.Join(", ", listed[..^1]) + " and " + listed[^1];
            return brief ? " · " + joined : " · " + joined + "; not all session observations";
        }

        if (ladder.Current.Level == DetailLevel.ProcessInstance && rpcChannelRows.Count > 0)
        {
            return brief ? " · paired TCP and RPC calls"
                : " · admitted paired TCP and this process's RPC calls by interface; not all session observations";
        }

        return brief ? " · paired TCP only" : " · admitted paired TCP only; not all session observations";
    }

    /// <summary>
    /// The channel a published session's channel rung is focused on. Its rung lists no operations, so its own record
    /// count, not a sum of absent rows, is what the rung states.
    /// </summary>
    private Channel? FocusedRealChannel => realOverview && ladder.Current.Level == DetailLevel.Channel
        && ladder.Current.Focus is { } focus
            ? Snapshot.Channels.FirstOrDefault(channel => channel.Key == focus.Key)
            : null;

    /// <summary>Why this rung is empty, naming the source that would supply it. Empty when it has rows.</summary>
    public string EmptyReason
    {
        get
        {
            if (IsEvidenceRung)
            {
                if (evidence is null || evidence.Records.Count > 0) return string.Empty;
                return evidence.Problem
                    ?? (evidence.Loading
                        ? "Reading this scope's source records…"
                        : "No admitted source record is in this scope. That is not proof of inactivity: coverage "
                            + "is stated in the health strip, and a time brush or a removed filter changes the scope.");
            }

            if (IsRpcChannelRung)
            {
                if (rpcCallRows.Count > 0) return string.Empty;
                return rpcCalls?.Problem
                    ?? (rpcCalls is null || rpcCalls.Loading
                        ? "Reading this channel's calls…"
                        : "This channel has no call in this generation. Its records are one step away.");
            }

            if (IsHttpChannelRung)
            {
                if (httpExchangeRows.Count > 0) return string.Empty;
                return httpExchanges?.Problem
                    ?? (httpExchanges is null || httpExchanges.Loading
                        ? "Reading this process's HTTP exchanges…"
                        : "This process has no HTTP exchange in this scope. Its records are one step away.");
            }

            if (view.EmptyReason is null || rpcChannelRows.Count > 0 || httpChannelRows.Count > 0 || ConnectionCount > 0)
            {
                return string.Empty;
            }
            if (emptyWorkspace) return awaitingCaptureNote ?? "No capture is running. Start exploring to publish a live session.";
            if (realOverview && ladder.Current.Level == DetailLevel.ProcessInstance && rpcChannels is { Loading: true })
            {
                return "Reading this process's RPC calls…";
            }

            if (realOverview && ladder.Current.Level == DetailLevel.ProcessInstance && httpChannels is { Loading: true })
            {
                return "Reading this process's HTTP exchanges…";
            }

            if (realOverview && ladder.Current.Level == DetailLevel.ProcessInstance && connections is { Loading: true })
            {
                return "Reading this process's connections…";
            }

            if (realOverview && ladder.Current.Level is DetailLevel.Channel or DetailLevel.Operation)
            {
                return "TCP records are completed transfers, not operations with a start and an end, so a channel "
                    + "has no operation rung. Its source records are one step away.";
            }

            if (realOverview && ladder.Current.Level == DetailLevel.Evidence)
            {
                return "Source records need the session directory this workspace was opened from.";
            }

            if (realOverview && ladder.Current.Level == DetailLevel.ProcessInstance)
            {
                return Snapshot.ChannelProjectionProblem is { } problem
                    ? problem + " Choose Browse paired channels here to page this process's admitted channels "
                        + "and inspect a selected channel's source rows."
                    : "No admitted paired TCP channel belongs to this process in this generation. Its one-sided, "
                        + "ambiguous and other-mechanism records are still one step away.";
            }

            return view.EmptyReason;
        }
    }

    public bool IsEmptyRung => IsEvidenceRung ? evidenceRows.Count == 0
        : IsRpcChannelRung ? rpcCallRows.Count == 0
        : IsHttpChannelRung ? httpExchangeRows.Count == 0
        : view.EmptyReason is not null && rpcChannelRows.Count == 0 && httpChannelRows.Count == 0 && ConnectionCount == 0;

    /// <summary>Whether the empty rung can offer its one-step path to evidence as a button beside the reason.</summary>
    public bool OffersEvidenceStep => IsEmptyRung && realOverview && evidenceSource is not null
        && ladder.Current.Level is DetailLevel.ProcessInstance or DetailLevel.Channel or DetailLevel.Operation;

    public bool CanAscend => ladder.CanAscend;

    /// <summary>
    /// The back button's face: the rung it returns to by its kind alone, so a long name - a channel's two endpoints - never
    /// pushes the title and breadcrumb out of the header at the minimum width (R15). <see cref="AscendDetail"/> names it.
    /// </summary>
    public string AscendLabel => ladder.CanAscend
        ? $"Back to {NavigationState.Name(ladder.Breadcrumb[^2].Level)} (Esc)"
        : "Clear selection (Esc)";

    /// <summary>Where the back button returns, in full, as its tooltip and its accessible help say it.</summary>
    public string AscendDetail => ladder.CanAscend
        ? $"Back to {ladder.Breadcrumb[^2].Crumb} (Esc)"
        : "Clear the selection, at the machine rung (Esc)";

    /// <summary>Whether a forward step has a rung to re-enter: one an ascent or a crumb left (§6.7).</summary>
    public bool CanGoForward => ladder.CanGoForward;

    public string ForwardLabel => ladder.Forward is [NavigationState next, ..]
        ? $"Forward to {next.Crumb} (Alt+Right)"
        : "Nothing to go forward to (Alt+Right)";

    public string DescendHint => (IsEvidenceRung
        ? $"Enter opens the {RecordNoun} · M loads more · Esc goes back"
        : ladder.Current.Level == DetailLevel.Evidence
            ? "Evidence is the last rung. Esc returns to where you were."
            : "Enter opens the selected row · E jumps straight to evidence · Esc goes back")
        + (ladder.CanGoForward ? " · Alt+Right goes forward" : string.Empty);

    /// <summary>Whether the table equivalents are shown. They are always reachable, never a hidden mode.</summary>
    public bool ShowTables
    {
        get => showTables;
        set
        {
            if (showTables == value)
            {
                return;
            }

            showTables = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TableToggleLabel));
            FollowDescribedBytes();
            if (ReadsBytes)
            {
                // A shown table reads its rows' bytes, trying again one whose read failed; a hidden one reads nothing.
                intervalBytesFailure = null;
                RefreshIntervalRows(timelineFocusBuckets);
            }
        }
    }

    public string TableToggleLabel => showTables ? "Hide tables (T)" : "Show tables (T)";

    /// <summary>The ranked row the user is on. Selecting it does not descend; Enter does (section 3.2).</summary>
    public RungRow? SelectedRung
    {
        get => selectedRung;
        set
        {
            selectedRung = value;
            OnPropertyChanged();
            if (IsEvidenceRung)
            {
                SelectEvidence(value?.Key);
                return;
            }

            if (value is not null) ForgetRelationship();
            describesRow = false;
            if (value is not null && TryResolveProcess(value.Key, out ProcessNode? process))
            {
                SelectedProcess = process;
            }
            else if (value is not null && ladder.Current.Level == DetailLevel.Machine
                && wholeSnapshot.Groups.Any(group => group.Key == value.Key))
            {
                // A machine-rung row is an executable group: the graph rings where its members are drawn.
                SelectGroup(value.Key, syncRow: false);
            }
            else
            {
                // A channel or a call is no graph node: the inspector describes the row itself, which the rail cuts short.
                describesRow = value is not null;
            }

            // A channel chosen among a process's rows is highlighted; the process and group paths above already update it.
            UpdateHighlight();
            RaiseSelectionDescribed();
        }
    }

    /// <summary>
    /// The ranked row the inspector describes: the selected row, when it is what the user chose last and neither a process
    /// nor a group - a channel, an RPC channel or one of its calls - whose name and detail the narrow rail cuts short.
    /// </summary>
    public RungRow? DescribedRow => describesRow && !IsEvidenceRung && !HasMultiSelection ? selectedRung : null;

    /// <summary>What the described row's keys do: Enter's step, and O's where its call is linked to one at its other end.</summary>
    public string SelectionActions
    {
        get
        {
            if (selectedRelationship is { } chosen) return $"A double click on its edge, or Enter on its row, opens {chosen.Opens}";
            if (DescribedRow is not { } row) return string.Empty;
            string enter = RpcChannelKeys.TryParseChannel(row.Key, out _, out _, out _) ? "Enter lists its calls"
                : row.Source.DescendsTo switch
                {
                    DetailLevel.Evidence => "Enter opens its records",
                    DetailLevel.Channel => "Enter opens the channel",
                    DetailLevel.Operation => "Enter opens the operation",
                    _ => "Enter opens it",
                };
            return row.OtherEndCall is { } other ? $"{enter} · O opens {other}" : enter;
        }
    }

    public bool HasSelectionActions => SelectionActions.Length > 0;

    /// <summary>A described row in full: its label where the rail shows it by more than its name, then its detail.</summary>
    private static string DescribeRow(RungRow row)
    {
        string lead = row.Source.Label.Contains(row.Label, StringComparison.Ordinal) ? string.Empty : row.Label;
        string detail = row.DetailLine;
        return lead.Length == 0 ? detail : detail.Length == 0 ? lead : $"{lead} · {detail}";
    }

    private void RaiseSelectionDescribed()
    {
        OnPropertyChanged(nameof(DescribedRow));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(EvidenceHeading));
        OnPropertyChanged(nameof(EvidenceSummary));
        FollowDescribedBytes();
        OnPropertyChanged(nameof(SelectionActions));
        OnPropertyChanged(nameof(HasSelectionActions));
        RaiseLineageChanged();
    }

    public CrumbRow? SelectedCrumb
    {
        get => selectedCrumb;
        set
        {
            selectedCrumb = value;
            OnPropertyChanged();
            if (value is not null && !value.IsCurrent)
            {
                ReturnTo(value.Depth);
            }
        }
    }

    public FilterRow? SelectedFilter
    {
        get => selectedFilter;
        set
        {
            selectedFilter = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The relationship chosen in the table, or by a click on its edge (§6.7): the selection while it is the latest choice.
    /// Its edge is haloed, the inspector describes it, and the timeline highlights its records - its channel's when it rests
    /// on one, its linked calls when calls link it. Choosing anything else, or moving to another rung, lets it go; a new
    /// count or bytes read restate its row, never the choice.
    /// </summary>
    public RelationshipRow? SelectedRelationship
    {
        get => selectedRelationship;
        set
        {
            // While the table restates its rows the list lets its choice go for a moment; the choice is kept by key instead.
            if (restatingRelationships || ReferenceEquals(selectedRelationship, value))
            {
                return;
            }

            if (value is null)
            {
                // Unchosen in the table, it takes its edge's halo and its records' highlight with it.
                ForgetRelationship();
                return;
            }

            // The relationship is the selection now, in place of a process, group, aggregate, set or described row.
            SelectedProcess = null;
            selectedClusterKey = null;
            selectedGroupKey = null;
            chosenProcesses.Clear();
            graphSelection = null;
            describesRow = false;
            chosenChannelKey = OnlyChannelOf(value.Key);
            selectedRelationship = value;
            RaiseRelationshipChosen();
        }
    }

    /// <summary>
    /// §6.7's click on an edge: chooses the one relationship it stands for, as choosing its row in the table does. An
    /// aggregate edge stands for several and chooses none.
    /// </summary>
    public bool SelectGraphEdge(string edgeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeKey);
        if (graphDisplay.Edges.FirstOrDefault(edge => edge.Key == edgeKey) is not { Relationships.Count: 1 } drawn
            || relationships.FirstOrDefault(row => row.Key == drawn.Relationships[0]) is not { } row)
        {
            return false;
        }

        SelectedRelationship = row;
        return true;
    }

    /// <summary>
    /// The records of the chosen relationship as one scope, which E reads at the machine rung: the calls its links join, by
    /// its own key, or the one channel it rests on. Null with none chosen, and for one resting on several channels, which no
    /// one scope names; its row opens its source process, whose rows list them.
    /// </summary>
    private LadderTarget? ChosenRelationshipRecords() =>
        selectedRelationship is not { } chosen ? null
        : RpcChannelKeys.IsRpc(chosen.Key) ? new(DetailLevel.Channel, chosen.Key, LinkedCalls(chosen.Source, chosen.Target))
        : chosenChannelKey is { } only && wholeSnapshot.Channels.FirstOrDefault(channel => channel.Key == only) is { } channel
            ? new(DetailLevel.Channel, channel.Key, channel.Name)
            : null;

    /// <summary>How an RPC relationship's calls are named where its records are read: by the two processes its links join.</summary>
    private static string LinkedCalls(string source, string target) => $"RPC calls linked between {source} and {target}";

    /// <summary>The one channel a relationship rests on, whose records the timeline then highlights; null for none or several.</summary>
    private string? OnlyChannelOf(string relationshipKey)
    {
        string? only = null;
        foreach (Channel channel in wholeSnapshot.Channels)
        {
            if (!string.Equals(channel.EdgeKey, relationshipKey, StringComparison.Ordinal)) continue;
            if (only is not null) return null;
            only = channel.Key;
        }

        return only;
    }

    /// <summary>
    /// Lets the chosen relationship go, as choosing anything else or moving to another rung does, with the channel whose
    /// records it highlighted. It restates only what described the relationship and the timeline's highlight; the choice
    /// that replaces it says what it is itself.
    /// </summary>
    private void ForgetRelationship()
    {
        if (selectedRelationship is null) return;
        selectedRelationship = null;
        chosenChannelKey = null;
        RaiseRelationshipDescribed();
        OnPropertyChanged(nameof(HighlightedEdgeKey));
        UpdateHighlight();
    }

    /// <summary>
    /// Rebuilds the relationship table's rows for a new count or bytes read, keeping the relationship chosen in it by key:
    /// the rows are restated, never the choice. A brush keeps every relationship listed, but one no longer listed would
    /// let the choice go.
    /// </summary>
    private void RestateRelationships()
    {
        string? chosen = selectedRelationship?.Key;
        relationships = RelationshipRows();
        RelationshipRow? kept = chosen is null ? null : relationships.FirstOrDefault(row => row.Key == chosen);
        restatingRelationships = true;
        try
        {
            OnPropertyChanged(nameof(Relationships));
        }
        finally
        {
            restatingRelationships = false;
        }

        if (chosen is null)
        {
            return;
        }

        if (kept is null)
        {
            ForgetRelationship();
            return;
        }

        selectedRelationship = kept;
        RaiseRelationshipDescribed();
    }

    /// <summary>A person's choice of a relationship: the graph, the inspector and the timeline's highlight all follow it.</summary>
    private void RaiseRelationshipChosen()
    {
        RaiseRelationshipDescribed();
        OnPropertyChanged(nameof(HighlightedEdgeKey));
        RaiseGraphSelectionChanged();
    }

    /// <summary>What states the chosen relationship: its row's choice in the table, the line beneath it, and the inspector.</summary>
    private void RaiseRelationshipDescribed()
    {
        OnPropertyChanged(nameof(SelectedRelationship));
        OnPropertyChanged(nameof(SelectedRelationshipExplanation));
        OnPropertyChanged(nameof(HasSelectedRelationship));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(SelectionActions));
        OnPropertyChanged(nameof(HasSelectionActions));
        OnPropertyChanged(nameof(EvidenceHeading));
        OnPropertyChanged(nameof(EvidenceSummary));
    }

    /// <summary>
    /// The selected relationship in words, beneath the table: what its evidence's tooltip says - the rule that derived it
    /// and what it rests on - for a person who chose it by keyboard as for one who points at it (R4, §6.2), and what Enter
    /// opens, as its edge's card says of a double click (R15).
    /// </summary>
    public string SelectedRelationshipExplanation => selectedRelationship is { } row
        ? $"{row.Explanation} Enter opens {row.Opens}."
        : string.Empty;

    public bool HasSelectedRelationship => selectedRelationship is not null;

    public IntervalRow? SelectedIntervalRow
    {
        get => selectedIntervalRow;
        set
        {
            selectedIntervalRow = value;
            OnPropertyChanged();
            if (value is not null)
            {
                SelectInterval(value.Interval);
            }
        }
    }

    public ProcessNode? SelectedProcess
    {
        get => selectedProcess;
        set
        {
            if (selectedProcess == value)
            {
                return;
            }

            selectedProcess = value;
            if (value is not null)
            {
                selectedClusterKey = null;
                selectedGroupKey = null;
                chosenChannelKey = null;
                chosenProcesses.Clear();
                graphSelection = null;
                describesRow = false;
                ForgetRelationship();
            }

            OnPropertyChanged();
            selection.SelectProcess(value?.Id);
            RaiseGraphSelectionChanged();
            if (ladder.Current.Level == DetailLevel.Group)
            {
                RefreshIntervalRows(timelineFocusBuckets);
                OnPropertyChanged(nameof(HasSelectedProcessLane));
            }
        }
    }

    public TimeRange? SelectedInterval => selectedInterval;

    public string SelectionTitle => selectedRelationship is { } chosen ? $"{chosen.Source} ↔ {chosen.Target}"
        : DescribedRow is { } row ? row.Source.Label
        : HasMultiSelection ? ProcessSetFilter.Label(chosenProcesses.Count)
        : SelectedCluster is { } cluster ? cluster.Label
        : SelectedGroup is { } group ? group.Name
        : selectedProcess is null ? "Nothing selected" : selectedProcess.Name;

    public string SelectionSubtitle => selectedRelationship is { } chosen ? $"{chosen.Mechanism} · {chosen.Explanation}"
        : DescribedRow is { } row ? DescribeRow(row)
        : HasMultiSelection ? DescribeChosen()
        : SelectedCluster is { } cluster ? DescribeCluster(cluster)
        : SelectedGroup is { } group ? DescribeGroup(group)
        : selectedProcess is null
            ? "Choose a node, ranked row, or timeline bucket."
            : $"{selectedProcess.PidLabel} · {selectedProcess.Role}";

    /// <summary>What an aggregate node holds, and where each of its processes can be read one by one.</summary>
    /// <summary>What a multi-selection holds, by name, and the two gestures that change or apply it (§6.7).</summary>
    private string DescribeChosen()
    {
        IReadOnlyList<ProcessNode> chosen = ChosenProcesses;
        string names = string.Join(", ", chosen.Take(3).Select(process => process.NameWithPid));
        if (chosen.Count > 3)
        {
            names += string.Create(CultureInfo.CurrentCulture, $" and {chosen.Count - 3:N0} more");
        }

        return $"{names} · Ctrl+click or Ctrl+Space adds or removes · Enter lists their records";
    }

    private string DescribeCluster(GraphDisplayNode cluster)
    {
        string members = Counted(cluster.Members.Count, "process", "processes");
        string relationships = cluster.Relationships == 0
            ? "no relationship"
            : Counted(cluster.Relationships, "relationship", "relationships")
                + (cluster.InternalRelationships > 0
                    ? string.Create(CultureInfo.CurrentCulture, $", {cluster.InternalRelationships:N0} among themselves")
                    : string.Empty);
        int quiet = cluster.Members.Count(member => !relatedProcesses.Contains(member));
        return cluster.Kind switch
        {
            GraphNodeKind.Quiet => $"{members} with no admitted relationship in this session, counted in one node rather "
                + "than drawn one by one. The ranked table lists each of them.",
            GraphNodeKind.OtherMembers => $"{members} of this group not drawn on their own"
                + (quiet == cluster.Members.Count ? ", none with a relationship"
                    : quiet > 0 ? string.Create(CultureInfo.CurrentCulture,
                        $": {quiet:N0} without a relationship, the rest its least active") : ", its least active")
                + $" · {relationships}. The ranked table lists each of them.",
            GraphNodeKind.Group => $"{members} · {relationships}. "
                + (ladder.Current.Level == DetailLevel.Machine ? "Enter opens the group." : "Return to the machine rung to open it."),
            GraphNodeKind.Context => $"{members} outside this focus, counted rather than drawn · "
                + DescribeContextRelationships(cluster) + ". Esc returns to the wider graph.",
            _ => $"{members} folded together to keep the graph readable · {relationships}. The ranked table lists each of them.",
        };
    }

    /// <summary>
    /// A context node's relationships, split the way the canvas shows them: those with a drawn process are drawn as faded
    /// edges into it, and those among its own processes are folded inside it, so a context node with no edge still says
    /// what it holds.
    /// </summary>
    private static string DescribeContextRelationships(GraphDisplayNode context)
    {
        int crossing = context.Relationships - context.InternalRelationships;
        string drawn = crossing == 0
            ? "no relationship with a drawn process"
            : Counted(crossing, "relationship", "relationships") + " with the drawn processes";
        return context.InternalRelationships == 0
            ? drawn
            : drawn + string.Create(CultureInfo.CurrentCulture, $", {context.InternalRelationships:N0} among themselves");
    }

    /// <summary>
    /// The hover card for a drawn node or edge key (§6.2's hover contract, §6.3). It states what the mark stands for, the
    /// exact scope its numbers answer, the metric and its value, what is unmeasured, its evidence and coverage, and the
    /// scale its size or thickness is read against. Hover only describes: it never changes selection or filters.
    /// </summary>
    public HoverCard? DescribeGraphHover(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        bool linkedCalls = realOverview && Snapshot.Edges.Any(edge => edge.Mechanism == Mechanism.Rpc);
        string metric = linkedCalls ? "Paired TCP observations and linked RPC call records"
            : realOverview ? "Paired TCP observations" : "Observations";
        string semantics = linkedCalls
            ? "Basis: source observations · unit: records · domain: paired TCP transport evidence and RPC calls linked through ALPC · accounting: not applicable to a count; both ends contribute"
            : realOverview
            ? "Basis: source observations · unit: observations · domain: paired TCP transport evidence · accounting: not applicable to a count; both witnessed endpoints contribute"
            : "Basis: source observations · unit: observations · domain: relationship evidence · accounting: not applicable to a count";
        string scope = appliedInterval is { } interval
            ? "Scope: " + WorkspaceTime.FormatHalfOpenRange(interval, CultureInfo.CurrentCulture) + " · brushed interval"
            : "Scope: " + WorkspaceTime.FormatHalfOpenRange(Snapshot.Extent, CultureInfo.CurrentCulture) + " · whole session";
        GraphDisplay drawnDisplay = GraphDisplay;
        string magnitude = drawnDisplay.Edges.Any(edge => edge.Magnitude is not null) ? "bytes sent across its relationships" : "records";
        if (drawnDisplay.Node(key) is { } node)
        {
            long scale = GraphEncoding.NodeScale(drawnDisplay);
            string title = node.Title;
            string what = node.Kind switch
            {
                GraphNodeKind.Process => "Process instance · " + Counted(node.Relationships, "relationship", "relationships"),
                GraphNodeKind.Group => Counted(node.Members.Count, "process", "processes") + " of this executable, drawn as one node · "
                    + Counted(node.Relationships, "relationship", "relationships"),
                GraphNodeKind.OtherMembers => Counted(node.Members.Count, "member", "members") + " of the opened group not drawn on their own",
                GraphNodeKind.Remainder => Counted(node.Members.Count, "less active process", "less active processes") + " folded together",
                GraphNodeKind.Context => Counted(node.Members.Count, "process", "processes") + " outside this focus, counted rather than drawn",
                _ => Counted(node.Members.Count, "process", "processes") + " with no admitted relationship",
            };
            HashSet<ProcessInstanceId> members = [.. node.Members];
            CoverageState coverage = Worst(Snapshot.Processes.Where(process => members.Contains(process.Id))
                .Select(process => process.Coverage));
            var nodeLines = new List<string>
            {
                what,
                scope,
                semantics,
                string.Create(CultureInfo.CurrentCulture, $"{metric} on its relationships: {node.Observations:N0}"),
                // An RPC edge's calls carry no size: its records are neither measured bytes nor unknown ones.
                HoverBytes(DescribedEdges(Snapshot.Edges.Where(edge => edge.Mechanism != Mechanism.Rpc
                        && (members.Contains(edge.SourceId) || members.Contains(edge.TargetId)))),
                    node.Observations - Snapshot.Edges
                        .Where(edge => edge.Mechanism == Mechanism.Rpc && (members.Contains(edge.SourceId) || members.Contains(edge.TargetId)))
                        .Sum(edge => edge.ObservationCount)),
                "Coverage: " + DescribeCoverage(coverage),
                node.Kind == GraphNodeKind.Context
                    ? "Size: fixed; the rest of the machine is not read on this focus's scale"
                    : node.Unmeasured
                    ? "Size: unknown · its relationships' sends recorded no size, so it is drawn open and cross-hatched at the "
                        + "smallest size, not as zero"
                    : magnitude == "records"
                        ? string.Create(CultureInfo.CurrentCulture, $"Size: log scale against the busiest drawn node, {scale:N0}")
                        : $"Size: {magnitude}, log scale against the busiest drawn node, {WorkspaceRowBuilder.DescribeSize(scale)}",
            };
            if (graphPins.ContainsKey(node.Key))
            {
                nodeLines.Add("Pinned where it was placed · P releases it");
            }

            // What OpenGraphNode will do, so the gesture is discoverable where the node is read, as an edge's is.
            if (node.Kind == GraphNodeKind.Group && ladder.Current.Level == DetailLevel.Machine)
            {
                nodeLines.Add("Double-click or Enter opens the group");
            }
            else if (OpensProcess(node))
            {
                nodeLines.Add("Double-click or Enter opens this process");
            }

            return new(title, nodeLines);
        }

        if (drawnDisplay.Edges.FirstOrDefault(edge => edge.Key == key) is not { } drawn)
        {
            return null;
        }

        long edgeScale = GraphEncoding.EdgeScale(drawnDisplay);
        HashSet<string> relationships = [.. drawn.Relationships];
        Channel[] channels = [.. Snapshot.Channels.Where(channel => relationships.Contains(channel.EdgeKey))];
        if (drawn.Mechanism == Mechanism.Rpc)
        {
            // An RPC edge is calls linked through ALPC, not a transport: it counts call records and carries no size.
            metric = "Linked RPC call records";
            semantics = "Basis: source observations · unit: records · domain: RPC calls linked to the calls that served them "
                + "through ALPC (rpc-call-peer-v1) · accounting: both ends' call records; the ALPC records are the link's "
                + "evidence, not counted";
        }
        var lines = new List<string>
        {
            $"{ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(drawn.Mechanism)).Label} · {DescribeStrength(drawn.Strength)} evidence · "
                + Counted(drawn.Relationships.Count, "relationship", "relationships") + " · "
                + Counted(channels.Length, "channel", "channels"),
        };

        // The rule that derived the mark, and what it rests on (R4): no edge is drawn that its card cannot explain.
        CommunicationEdge[] made = [.. Snapshot.Edges.Where(edge => relationships.Contains(edge.Key))];
        if (made.Length > 0)
        {
            bool calls = made.All(edge => edge.Rule == RelationRule.RpcCallPeer);
            lines.Add("Rule: " + string.Join("; ", made.Select(edge => edge.Rule).Distinct().Select(WorkspaceRowBuilder.DescribeRule))
                + " · evidence: " + (calls
                    ? "the calls its links join, by key"
                    : Counted(made.Sum(edge => edge.Evidence.Count), "channel", "channels") + " by key"));
        }

        lines.AddRange(
        [
            scope,
            semantics,
            string.Create(CultureInfo.CurrentCulture, $"{metric}: {drawn.ObservationCount:N0}")
                + (realOverview ? " · from both ends" : string.Empty)
                + (drawn.ObservationCount == 0 && appliedInterval is not null
                    ? " · none in this interval; the relationship exists in the session"
                    : string.Empty),
            drawn.Mechanism == Mechanism.Rpc
                ? "Bytes: none · an RPC call carries no size"
                : HoverBytes(DescribedEdges(Snapshot.Edges.Where(edge => relationships.Contains(edge.Key))), drawn.ObservationCount),
        ]);
        if (realOverview)
        {
            lines.Add(drawn.Mechanism == Mechanism.Rpc
                ? "Direction: display order only; each end's RPC rows say which calls and which served"
                : "Direction: display order only, not who initiated or sent");
        }

        lines.Add("Coverage: " + DescribeCoverage(drawn.Mechanism == Mechanism.Rpc ? RpcEdgeCoverage()
            : channels.Length == 0 ? CoverageState.UnknownCoverage : Worst(channels.Select(channel => channel.Coverage))));
        lines.Add(drawn.Unmeasured
            ? "Thickness: none · its sends recorded no size, so it is drawn as an open cross-hatched band, unknown rather than zero"
            : magnitude == "records"
            ? string.Create(CultureInfo.CurrentCulture, $"Thickness: log scale against the busiest drawn edge, {edgeScale:N0}")
            : $"Thickness: bytes sent across, log scale against the busiest drawn edge, {WorkspaceRowBuilder.DescribeSize(edgeScale)}");
        if (drawn.Relationships.Count == 1 && made is [{ } only])
        {
            // What OpenGraphEdge will do, so the gesture is discoverable where the edge is read; the relationship's row in
            // the table says the same of Enter.
            lines.Add("Double-click opens " + WorkspaceRowBuilder.Opens(Snapshot, only));
        }

        // Each end named as its node's card names it, and as the relationship table names a process: by name and PID, so
        // an edge between two instances of one executable never reads "chrome.exe ↔ chrome.exe".
        string source = graphDisplay.Node(drawn.SourceKey)?.Title ?? drawn.SourceKey;
        string target = graphDisplay.Node(drawn.TargetKey)?.Title ?? drawn.TargetKey;
        return new($"{source} ↔ {target}", lines);
    }

    /// <summary>
    /// An RPC edge's coverage: the worst of the RPC and ALPC lanes over the drawn session, since a link needs both; unknown
    /// where the timeline holds neither lane.
    /// </summary>
    private CoverageState RpcEdgeCoverage()
    {
        CoverageState[] states = [.. Snapshot.MechanismLanes
            .Where(lane => lane.Mechanism is Mechanism.Rpc or Mechanism.Alpc)
            .SelectMany(lane => lane.Buckets)
            .Select(bucket => bucket.Coverage)];
        return states.Length == 0 ? CoverageState.UnknownCoverage : Worst(states);
    }

    /// <summary>
    /// Bytes a mark's relationships measured, and how many observations belong to relationships whose byte total is
    /// unknown. The model has relationship-level byte availability, not a per-observation byte-known bit, so the card
    /// states that granularity rather than fabricating a more precise denominator (R21).
    /// </summary>
    private string HoverBytes(IEnumerable<CommunicationEdge> edges, long observations)
    {
        CommunicationEdge[] all = [.. edges];
        CommunicationEdge[] known = [.. all.Where(edge => edge.KnownBytes.HasValue)];
        long unknownObservations = all.Where(edge => !edge.KnownBytes.HasValue).Sum(edge => edge.ObservationCount);

        // A real session's overview sums no bytes: they are read once something is selected, and then each relationship
        // carries what was sent across it, each transfer counted once at its sender.
        if (ReadsBytes && DescribedBytes is null)
        {
            return "Bytes: not read yet · selecting reads them for this scope";
        }

        if (ReadsBytes && DescribedBytes is { } read)
        {
            // What was sent across the relationships, each transfer once at its sender: a measured sum, sends that recorded
            // no size, or none at all. Unknown is not zero, and none sent is not unknown (R21).
            Dictionary<string, SentAcrossTally> sent = SentAcrossEdges(read);
            SentAcrossTally across = all.Aggregate(default(SentAcrossTally), (sum, edge) => sum.Plus(sent.GetValueOrDefault(edge.Key)));
            string unmeasured = across.Unmeasured == 1 ? "1 send recorded no size"
                : string.Create(CultureInfo.CurrentCulture, $"{across.Unmeasured:N0} sends recorded no size");
            return across.Measured > 0
                ? "Bytes: " + WorkspaceRowBuilder.DescribeSize(across.Sent) + " sent across, each transfer counted once at its sender"
                    + (across.Unmeasured > 0 ? " · " + unmeasured : string.Empty)
                : across.Unmeasured > 0 ? "Bytes: unknown · " + unmeasured
                : "Bytes: nothing sent across";
        }

        if (known.Length == 0)
        {
            return observations == 0
                ? "Bytes: unknown · 0 contributing observations in this scope"
                : string.Create(CultureInfo.CurrentCulture,
                    $"Bytes: unknown · {unknownObservations:N0} contributing observations are on relationships with no byte total");
        }

        // DescribeBytes already says "known"; the prefix names the quantity only.
        string measured = "Bytes: " + WorkspaceRowBuilder.DescribeBytes(known.Sum(edge => edge.KnownBytes!.Value));
        return unknownObservations == 0
            ? measured + " · no contributing observation is on a relationship with an unknown byte total"
            : measured + string.Create(CultureInfo.CurrentCulture,
                $" · {unknownObservations:N0} contributing observations are on relationships with an unknown byte total");
    }

    /// <summary>
    /// The hover card for a timeline bucket as drawn (§6.2's hover contract). It states the bucket's exact half-open
    /// interval, what its bar counts and in which unit, its value, what the rung's focus holds of it, what is
    /// unmeasured, its coverage, and the scale its height is read against: <paramref name="peakPerSecond"/>, the busiest
    /// visible bar. Hover only describes; it never selects or brushes.
    /// </summary>
    public HoverCard DescribeTimelineHover(TimelineBucket bucket, double peakPerSecond,
        Mechanism? lane = null, ProcessNode? ownerLane = null, Direction? directionLane = null,
        ChannelEndTimelineLane? endLane = null)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (endLane is not null)
        {
            return DescribeChannelEndHover(bucket, peakPerSecond, endLane);
        }

        if (lane is { } byteLane && timelineBytes is { } plotted && ownerLane is null && directionLane is null)
        {
            // The lanes plot bytes, so the card states what a bar plots and the byte rate its height reads against.
            return DescribeLaneBytesHover(bucket, peakPerSecond, byteLane, plotted);
        }

        if (processLaneBytes is { } group && lane is null && directionLane is null && ShowsProcessLanes)
        {
            // A group's lanes and the machine row above them plot bytes too.
            return ownerLane is { } byteOwner
                ? DescribeOwnerBytesHover(bucket, peakPerSecond, byteOwner, group)
                : DescribeMachineBytesHover(bucket, peakPerSecond, group.Metric, group.Measures.Machine, group.Measures.Generation);
        }

        if (directionBytes is { } rows && lane is null && ownerLane is null && ShowsDirectionLanes)
        {
            // So do a process's direction rows and the machine row above them.
            return directionLane is { } byteDirection
                ? DescribeDirectionBytesHover(bucket, peakPerSecond, byteDirection, rows)
                : DescribeMachineBytesHover(bucket, peakPerSecond, rows.Metric, rows.Measures.Machine, rows.Measures.Generation);
        }

        bool zoomed = timelineDetail is { } detail && (detail.Buckets.Contains(bucket)
            || detail.MechanismLanes.Any(candidate => candidate.Mechanism == lane && candidate.Buckets.Contains(bucket))
            || ownerLane is not null && processLaneDisplay.Any(candidate =>
                candidate.ProcessId == ownerLane.Id && candidate.Buckets.Contains(bucket))
            || directionLane is { } zoomedDirection && timelineDirectionLanes is { } directionRows
                && directionRows.Any(candidate => candidate.Direction == zoomedDirection && candidate.Buckets.Contains(bucket)));
        string mechanism = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(bucket.DominantMechanism)).Label;
        double perSecond = (double)bucket.ObservationCount * WorkspaceTime.TicksPerSecond / Math.Max(1, bucket.Interval.SpanTicks);
        var lines = new List<string>
        {
            bucket.ObservationCount == 0
                ? bucket.Coverage == CoverageState.Covered
                    ? "No record observed · the capture covered this interval, so nothing it collects happened here"
                    : "No record observed · an empty bucket is not proof of inactivity"
                : Counted(bucket.ObservationCount, "observed record", "observed records")
                    + (ownerLane is { } owner
                        ? $" · {owner.NameWithPid} lane"
                        : directionLane is { } countedDirection ? $" · {DirectionLabel(countedDirection)} lane"
                        : lane is { } selected ? $" · {EvidenceRowText.MechanismName(selected)} lane" : $" · mostly {mechanism}"),
            ownerLane is { } ownerProcess
                ? $"Basis: source observations · unit: records · domain: records canonically owned by {ownerProcess.NameWithPid}, instance {ownerProcess.Id} · accounting: one owner per record"
                : directionLane is { } rowDirection
                ? $"Basis: source observations · unit: records · domain: {LowerFirst(timelineFocusDescription ?? "the instance's records")} {SourceDirectionDomain(rowDirection)} · accounting: one owner and one source direction per record"
                : lane is { } laneMechanism
                ? $"Basis: source observations · unit: records · domain: {EvidenceRowText.MechanismName(laneMechanism)} records with session time · accounting: not applicable to a count"
                : realOverview
                ? "Basis: source observations · unit: records · domain: every admitted record with a session time, all "
                    + "mechanisms · accounting: not applicable to a count"
                : "Basis: source observations · unit: records · domain: the tour's records · accounting: not applicable to a count",
        };

        if (directionLane is { } meaning)
        {
            lines.Add(DescribeSourceDirection(meaning));
        }

        if (timelineFocusDescription is { } focus && TimelineShowsFocus)
        {
            lines.Add(timelineFocusBuckets?.FirstOrDefault(candidate => candidate.Interval == bucket.Interval) is { } focused
                ? ownerLane is not null
                    ? $"{focus}: " + Counted(focused.ObservationCount, "record", "records")
                        + " in this interval across the group"
                    : directionLane is not null
                    ? $"{focus}: " + Counted(focused.ObservationCount, "record", "records")
                        + " in this interval across all directions"
                    : $"{focus}: " + Counted(focused.ObservationCount, "record", "records") + " of them"
                : $"{focus}: being counted");
        }

        if (highlight is not null && highlightName is { } marked && ownerLane is null && directionLane is null)
        {
            // The selection's share of this bar (§6.4), from its own count on the drawn columns; a bar of another
            // resolution is not given a number it was not counted at.
            string share = highlightBuckets is null ? "being counted"
                : highlightBuckets.FirstOrDefault(candidate => candidate.Interval == bucket.Interval) is not { } whole
                    ? "counted at another resolution"
                : lane is { } highlightedLane
                    ? Counted(highlightLanes?.FirstOrDefault(candidate => candidate.Mechanism == highlightedLane)
                        ?.Buckets.FirstOrDefault(candidate => candidate.Interval == bucket.Interval)?.ObservationCount ?? 0,
                        "record", "records") + " of them"
                    : Counted(whole.ObservationCount, "record", "records") + " of them";
            lines.Add($"Selection, {marked}: {share}");
        }

        lines.Add(ownerLane is not null || directionLane is not null
            ? $"Rate: {TimelineView.RateText(perSecond)} · "
                + HeightAgainst("the busiest visible lane including machine context", TimelineView.RateText(peakPerSecond))
            : lane is null && !(scalesEachLane && OffersLaneScale)
            ? $"Rate: {TimelineView.RateText(perSecond)} · height against the busiest visible bar, {TimelineView.RateText(peakPerSecond)}"
            : $"Rate: {TimelineView.RateText(perSecond)} · "
                + HeightAgainst("the busiest mechanism lane in this time view", TimelineView.RateText(peakPerSecond)));
        return FinishTimelineHover(bucket, zoomed, lines,
            ScopeOfLane(lane, ownerLane?.Id, directionLane, end: null),
            ownerLane is not null && CoarserLaneColumns is { } laneColumns
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Resolution: the lanes' own count, {laneColumns:N0} columns, coarser than the view's so the group's "
                    + $"{processLaneDisplay.Count:N0} lanes stay within {SessionTimelineQuery.MaximumProcessLaneCells:N0} cells")
                : null);
    }

    /// <summary>
    /// An L3 end lane's card. The end's records are split by source direction around its midline, so the card gives the
    /// bucket's outbound, inbound and undirected counts and where each is drawn; the channel's count spans both ends.
    /// </summary>
    private HoverCard DescribeChannelEndHover(TimelineBucket bucket, double peakPerSecond, ChannelEndTimelineLane end)
    {
        int index = Enumerable.Range(0, end.Buckets.Count).FirstOrDefault(
            candidate => end.Buckets[candidate].Interval == bucket.Interval, -1);
        int outbound = index < 0 ? 0 : end.Outbound[index].ObservationCount;
        int inbound = index < 0 ? 0 : end.Inbound[index].ObservationCount;
        int undirected = bucket.ObservationCount - outbound - inbound;
        long span = Math.Max(1, bucket.Interval.SpanTicks);
        string holder = ChannelEndHolder(end);
        var lines = new List<string>
        {
            bucket.ObservationCount == 0
                ? bucket.Coverage == CoverageState.Covered
                    ? "No record observed at this end · the capture covered this interval, so nothing it collects happened here"
                    : "No record observed at this end · an empty bucket is not proof of inactivity"
                : Counted(bucket.ObservationCount, "observed record", "observed records")
                    + $" · the {end.Endpoint} end, held by {holder}",
            $"Basis: source observations · unit: records · domain: records made at {end.Endpoint}, the end of this "
                + $"paired TCP channel that {holder} holds · accounting: each record at exactly one end",
            string.Create(CultureInfo.CurrentCulture,
                $"Direction: {outbound:N0} outbound, drawn above the midline · {inbound:N0} inbound, below · ")
                + string.Create(CultureInfo.CurrentCulture, $"{undirected:N0} with no data direction, marked on it"),
        };
        if (timelineFocusDescription is { } focus && TimelineShowsFocus)
        {
            lines.Add(timelineFocusBuckets?.FirstOrDefault(candidate => candidate.Interval == bucket.Interval) is { } focused
                ? $"{focus}: " + Counted(focused.ObservationCount, "record", "records") + " in this interval at both ends"
                : $"{focus}: being counted");
        }

        lines.Add($"Rate: {TimelineView.RateText((double)outbound * WorkspaceTime.TicksPerSecond / span)} outbound, "
            + $"{TimelineView.RateText((double)inbound * WorkspaceTime.TicksPerSecond / span)} inbound · each band's "
            + HeightAgainst("the busiest visible band including machine context", TimelineView.RateText(peakPerSecond)));
        return FinishTimelineHover(bucket, timelineDetail is not null && end.Buckets.Contains(bucket), lines,
            ScopeOfLane(null, null, null, end));
    }

    /// <summary>
    /// What every timeline card closes with: the unmeasured part, bytes, coverage, resolution and the click. A real
    /// session's bucket states its bytes where the lanes plot them, or where the interval table has read them for the same
    /// records and interval.
    /// </summary>
    private HoverCard FinishTimelineHover(TimelineBucket bucket, bool zoomed, List<string> lines, IntervalByteScope scope,
        string? resolution = null, string? unmeasured = null, TransportBytes? plotted = null)
    {
        lines.Add(unmeasured ?? "Unmeasured: none in this bucket; a record without a usable session time is placed in no bucket");
        lines.Add(bucket.KnownBytes is { } bytes ? "Bytes: " + WorkspaceRowBuilder.DescribeBytes(bytes)
            : plotted is not null ? "Bytes: " + WorkspaceRowBuilder.DescribeTransfers(plotted)
            : ReadBytesOf(scope, bucket.Interval) is { } read ? "Bytes: " + WorkspaceRowBuilder.DescribeTransfers(read)
            : ReadsBytes ? "Bytes: not summed by the timeline · the interval table (T) reads those of what it lists"
            : "Bytes: unknown · this timeline counts records");
        lines.Add("Coverage: " + DescribeCoverage(bucket.Coverage)
            + (bucket.Coverage == CoverageState.Covered ? string.Empty : " · drawn hatched"));
        lines.Add(resolution ?? (zoomed
            ? string.Create(CultureInfo.CurrentCulture, $"Resolution: this view's own count, {timelineDetail!.Buckets.Count:N0} buckets")
                + (timelineDetail.Generation != DisplayedGeneration
                    ? string.Create(CultureInfo.CurrentCulture, $" from generation {timelineDetail.Generation:N0}")
                    : string.Empty)
            : string.Create(CultureInfo.CurrentCulture, $"Resolution: the overview's {Snapshot.Timeline.Count:N0} buckets over the whole session")));
        lines.Add(selectedInterval == bucket.Interval
            ? "This bucket is the analysis interval"
            : "Click makes it the analysis interval · Shift+drag brushes a range");
        return new(WorkspaceTime.FormatHalfOpenRange(bucket.Interval, CultureInfo.CurrentCulture), lines);
    }

    private static CoverageState Worst(IEnumerable<CoverageState> states)
    {
        CoverageState worst = CoverageState.Covered;
        foreach (CoverageState state in states)
        {
            if (state > worst) worst = state;
        }

        return worst;
    }

    private static string DescribeCoverage(CoverageState coverage) => coverage switch
    {
        CoverageState.Covered => "covered",
        CoverageState.ReducedFidelity => "reduced fidelity",
        CoverageState.PartialGap => "partial gap, not extrapolated",
        CoverageState.NotCollected => "not collected",
        _ => "unknown",
    };

    /// <summary>The name an L2 source-direction row, its selector entry and its table scope share.</summary>
    internal static string DirectionLabel(Direction direction) => direction switch
    {
        Direction.Outbound => "Outbound",
        Direction.Inbound => "Inbound",
        Direction.Bidirectional => "Bidirectional",
        Direction.UnknownDirection => "Unknown direction",
        _ => "No data direction",
    };

    /// <summary>Which of the instance's records an L2 row holds, as the basis line's domain.</summary>
    private static string SourceDirectionDomain(Direction direction) => direction switch
    {
        Direction.Outbound => "marked outbound",
        Direction.Inbound => "marked inbound",
        Direction.Bidirectional => "marked bidirectional",
        Direction.UnknownDirection => "with no stated direction",
        _ => "with no data direction",
    };

    /// <summary>
    /// §6.6's direction word for an L2 row. The row follows each record's source-catalog direction (`EN-Direction`),
    /// which marks a connection attempt outbound and an accept inbound, so it never says who initiated a conversation;
    /// and a record the source gave no direction is never guessed into one.
    /// </summary>
    private static string DescribeSourceDirection(Direction direction) => direction switch
    {
        Direction.Outbound => "Direction: outbound, as the source marks sends, connection attempts and client calls · "
            + "it does not say who initiated the conversation",
        Direction.Inbound => "Direction: inbound, as the source marks receives, accepted connections and server calls · "
            + "it does not say who initiated the conversation",
        Direction.Bidirectional => "Direction: bidirectional, as the source marked these records",
        Direction.UnknownDirection => "Direction: unknown · the source stated none for these records, and none is inferred",
        _ => "Direction: none · process lifecycle records, disconnects and other records that carry no data",
    };

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string DescribeStrength(RelationStrength strength) => strength switch
    {
        RelationStrength.Direct => "direct",
        RelationStrength.Correlated => "correlated",
        RelationStrength.Candidate => "candidate",
        RelationStrength.Unresolved => "unresolved",
        _ => "conflicting",
    };

    /// <summary>A selected executable group: its size, where its members are drawn, and the path its name stands for.</summary>
    private string DescribeGroup(ProcessGroup group)
    {
        // Each member is counted once, by the node it is drawn in: on its own, in the group's collapsed node, or in an
        // aggregate - where a member without a relationship is named as such rather than as folded activity.
        ProcessNode[] members = [.. wholeSnapshot.Processes.Where(process => process.GroupKey == group.Key)];
        int own = 0;
        int collapsed = 0;
        int folded = 0;
        int quiet = 0;
        foreach (ProcessNode member in members)
        {
            switch (graphDisplay.NodeOf(member.Id)?.Kind)
            {
                case GraphNodeKind.Process:
                    own++;
                    break;
                case GraphNodeKind.Group:
                    collapsed++;
                    break;
                case null:
                    break;
                default:
                    if (relatedProcesses.Contains(member.Id)) folded++;
                    else quiet++;
                    break;
            }
        }

        var parts = new List<string> { Counted(members.Length, "process", "processes") };
        if (collapsed > 0)
        {
            parts.Add((collapsed == members.Length
                ? "drawn as one node"
                : string.Create(CultureInfo.CurrentCulture, $"{collapsed:N0} drawn as one node"))
                + (ladder.Current.Level == DetailLevel.Machine
                    ? "; Enter opens the group"
                    : "; the machine rung opens it"));
        }

        if (own > 0)
        {
            parts.Add(own == members.Length && members.Length == 1
                ? "drawn on its own"
                : string.Create(CultureInfo.CurrentCulture, $"{own:N0} drawn on their own"));
        }

        if (folded > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{folded:N0} in an aggregate"));
        }

        if (quiet > 0)
        {
            parts.Add(quiet == members.Length
                ? (members.Length == 1 ? "no relationship" : "none has a relationship")
                : string.Create(CultureInfo.CurrentCulture, $"{quiet:N0} without a relationship"));
        }

        // The path the short name stands for goes on its own line, where a long path can wrap without splitting a count.
        return string.Join(" · ", parts) + (group.Detail is { } detail ? Environment.NewLine + detail : string.Empty);
    }

    /// <summary>
    /// The time scope the inspector states: the analysis interval, or the whole session - its recording when its capture
    /// recorded a stop, which its rates divide by (metrics-v1 §7), or else the span of its records. An empty workspace has
    /// recorded no time, so its placeholder extent is never read out as a duration.
    /// </summary>
    public string IntervalLabel => selectedInterval is { } interval
        ? WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture)
        : VisibleScope is { } visible ? "Visible " + WorkspaceTime.FormatRange(visible, CultureInfo.CurrentCulture)
        : emptyWorkspace ? "No time recorded yet"
        : Snapshot.Recording is { } recording
            ? "All · " + WorkspaceTime.FormatDuration(recording.SpanTicks, CultureInfo.CurrentCulture) + " recorded"
        : "All " + WorkspaceTime.FormatDuration(Snapshot.Extent.EndTicks - Snapshot.Extent.StartTicks, CultureInfo.CurrentCulture);

    /// <summary>
    /// What the inspector's evidence line describes: the chosen relationship, processes or row, or the selected aggregate,
    /// group or process.
    /// </summary>
    public string EvidenceHeading => selectedRelationship is not null ? "Selected relationship"
        : HasMultiSelection ? "Selected processes"
        : ChosenRow is { } row ? "Selected " + ChosenRowNoun(row.Row)
        : SelectedCluster is not null ? "Selected aggregate"
        : SelectedGroup is not null ? "Selected group"
        : "Selected process";

    public string EvidenceSummary
    {
        get
        {
            // A chosen relationship's records as its row counts them, and what was sent across it; a call carries no size.
            if (selectedRelationship is { } chosen)
            {
                bool calls = RpcChannelKeys.IsRpc(chosen.Key);
                string counted = calls ? "linked RPC call records" : realOverview ? "paired TCP observations" : "observations";
                return string.Create(CultureInfo.CurrentCulture, $"{chosen.ObservationCount:N0} {counted} · ")
                    + (calls ? "an RPC call carries no size" : chosen.KnownBytes);
            }

            // A row chosen among a process's rows: its records, as E lists them.
            if (ChosenRow is { } row)
            {
                return ChosenRowEvidence(row.Row);
            }

            if (SelectedCluster is { } cluster)
            {
                string counted = Snapshot.Edges.Any(edge => edge.Mechanism == Mechanism.Rpc)
                    ? "paired TCP and linked RPC call records"
                    : "paired TCP observations";
                return cluster.Relationships == 0
                    ? "No admitted paired TCP relationship among these processes. Other activity may be present."
                    : string.Create(CultureInfo.CurrentCulture, $"{cluster.Observations:N0} {counted} on ")
                        + Counted(cluster.Relationships, "relationship", "relationships") + " · bytes unknown";
            }

            HashSet<ProcessInstanceId> scope = HasMultiSelection ? [.. chosenProcesses]
                : SelectedGroup is { } group
                ? [.. Snapshot.Processes.Where(process => process.GroupKey == group.Key).Select(process => process.Id)]
                : selectedProcess is { } process ? [process.Id] : [];
            if (scope.Count == 0)
            {
                return "No evidence selected";
            }

            // What the selection made comes first, as the ranked table counts it; what its relationships carry, from both
            // ends, follows, since the graph draws that.
            ProcessNode[] members = [.. Snapshot.Processes.Where(process => scope.Contains(process.Id))];
            CommunicationEdge[] edges = [.. Snapshot.Edges
                .Where(edge => scope.Contains(edge.SourceId) || scope.Contains(edge.TargetId))];
            long observations = edges.Sum(edge => edge.ObservationCount);
            string kind = realOverview
                ? edges.Any(edge => edge.Mechanism == Mechanism.Rpc) ? "paired TCP and linked RPC call records" : "paired TCP observations"
                : "observations";
            string relationships = edges.Length == 0
                ? realOverview ? "no admitted paired TCP relationship" : "no relationship"
                : realOverview && SelectedGroup is null && !HasMultiSelection
                    ? string.Create(CultureInfo.CurrentCulture, $"{observations:N0} {kind}")
                    : string.Create(CultureInfo.CurrentCulture, $"{observations:N0} {kind} on ")
                        + Counted(edges.Length, "relationship", "relationships");

            // A real session's bytes are the selection's own, read for the scope; the tour's are its relationships' known
            // totals. Bytes nothing measured are unknown, never zero (R3): a sum over no known value is no sum at all.
            if (ReadsBytes)
            {
                return OwnRecords(members) + " · " + relationships + " · " + SelectionBytes(members);
            }

            long? knownBytes = edges.Any(edge => edge.KnownBytes.HasValue)
                ? edges.Where(edge => edge.KnownBytes.HasValue).Sum(edge => edge.KnownBytes!.Value)
                : null;
            return OwnRecords(members) + " · " + relationships + " · " + WorkspaceRowBuilder.DescribeBytes(knownBytes);
        }
    }

    /// <summary>
    /// The records a selection made, as the ranked table counts them (`process-activity-v1`), and the mechanism they
    /// are: "1,234 own records, mostly TCP", or "all TCP" when every one is.
    /// </summary>
    private static string OwnRecords(IReadOnlyCollection<ProcessNode> members)
    {
        long own = members.Sum(member => member.Records);
        if (own == 0)
        {
            return "No own record admitted";
        }

        (Mechanism mechanism, long records) = members.SelectMany(member => member.Activity)
            .GroupBy(count => count.Mechanism)
            .Select(group => (Mechanism: group.Key, Records: group.Sum(count => count.Records)))
            .OrderByDescending(entry => entry.Records)
            .ThenBy(entry => entry.Mechanism)
            .First();
        string name = mechanism switch
        {
            // A lane may be called "Process"; in a sentence the records are the process's lifecycle.
            Mechanism.ProcessLifecycle => "process lifecycle",
            Mechanism.ThreadLifecycle => "thread lifecycle",
            _ => EvidenceRowText.MechanismName(mechanism),
        };
        return string.Create(CultureInfo.CurrentCulture,
            $"{own:N0} own {(own == 1 ? "record" : "records")}, {(records == own ? "all" : "mostly")} {name}");
    }

    /// <summary>Descends one rung from the selected row. One gesture, at every rung (section 3.2).</summary>
    public bool Descend()
    {
        if (chosenProcesses.Count > 0)
        {
            // Enter on a multi-selection is §6.7's Focus: the set becomes a filter.
            return ShowChosenRecords();
        }

        if (selectedRung is null)
        {
            return false;
        }

        LadderDescent descent = LadderProjection.DescentFor(
            selectedRung.Source,
            ladder.Current,
            selectedInterval ?? ladder.Current.Viewport);
        if (!TryDescend(descent))
        {
            return false;
        }

        AfterNavigation();
        return true;
    }

    /// <summary>The processes of §6.7's multi-selection, by PID and then instance, as they are named; empty without one.</summary>
    public IReadOnlyList<ProcessNode> ChosenProcesses => chosenProcesses.Count == 0
        ? []
        : [.. wholeSnapshot.Processes.Where(process => chosenProcesses.Contains(process.Id))
            .OrderBy(process => process.ProcessId).ThenBy(process => process.Id.Value)];

    /// <summary>Whether Ctrl+click has built a multi-selection.</summary>
    public bool HasMultiSelection => chosenProcesses.Count > 0;

    /// <summary>
    /// How much of what a ranked row stands for is in §6.7's multi-selection: a process row its process, a group row its
    /// processes, all of them or only some. Channel and record rows stand for no process of their own and are in none.
    /// </summary>
    public SelectionShare ShareOf(RungRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (chosenProcesses.Count == 0 || IsEvidenceRung)
        {
            return SelectionShare.None;
        }

        if (TryResolveProcess(row.Key, out ProcessNode? process))
        {
            return chosenProcesses.Contains(process!.Id) ? SelectionShare.All : SelectionShare.None;
        }

        if (ladder.Current.Level != DetailLevel.Machine)
        {
            return SelectionShare.None;
        }

        int members = 0, chosen = 0;
        foreach (ProcessNode member in wholeSnapshot.Processes)
        {
            if (!string.Equals(member.GroupKey, row.Key, StringComparison.Ordinal)) continue;
            members++;
            if (chosenProcesses.Contains(member.Id)) chosen++;
        }

        return chosen == 0 ? SelectionShare.None : chosen == members ? SelectionShare.All : SelectionShare.Some;
    }

    /// <summary>§6.7's Ctrl+click on a drawn node: adds its processes to the multi-selection, or removes them.</summary>
    public void ToggleGraphNodeInSelection(string key)
    {
        if (graphDisplay.Node(key) is { } node)
        {
            Toggle(node.Members);
        }
    }

    /// <summary>
    /// Ctrl+click or Ctrl+Space on a ranked row: adds its process, or a machine-rung group's processes, to the
    /// multi-selection, or removes them. Channel and record rows are not processes and add nothing.
    /// </summary>
    public void ToggleRungInSelection(RungRow? row)
    {
        if (row is null || IsEvidenceRung)
        {
            return;
        }

        if (TryResolveProcess(row.Key, out ProcessNode? process))
        {
            Toggle([process!.Id]);
        }
        else if (ladder.Current.Level == DetailLevel.Machine && wholeSnapshot.Groups.Any(group => group.Key == row.Key))
        {
            Toggle([.. wholeSnapshot.Processes
                .Where(member => string.Equals(member.GroupKey, row.Key, StringComparison.Ordinal))
                .Select(member => member.Id)]);
        }
    }

    /// <summary>
    /// Enter on a multi-selection (§6.7): lists exactly the chosen processes' records, the set being the evidence rung's
    /// visible, removable filter. The timeline there draws them in colour, as any rung's focus.
    /// </summary>
    public bool ShowChosenRecords()
    {
        ProcessInstanceId[] members = [.. ChosenProcesses.Select(process => process.Id)];
        if (members.Length == 0 || ladder.Current.Level == DetailLevel.Evidence)
        {
            return false;
        }

        TimeRange viewport = ScopeInterval ?? ladder.Current.Viewport;
        LadderDescent descent = LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
            new(DetailLevel.Group, ProcessSetFilter.KeyOf(members), ProcessSetFilter.Label(members.Length)),
            "Enter on a multi-selection scopes to exactly the processes chosen (§6.7).");
        if (!TryDescend(descent))
        {
            return false;
        }

        AfterNavigation();
        OnPropertyChanged(nameof(ChosenProcesses));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    /// <summary>
    /// Adds an entity's processes to the multi-selection, or removes them when every one is in it already. A single
    /// selection beside it becomes the set's first member, as a list's selected item does when another is Ctrl+clicked;
    /// from then on the set is the selection.
    /// </summary>
    private void Toggle(IReadOnlyCollection<ProcessInstanceId> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        if (chosenProcesses.Count == 0)
        {
            chosenProcesses.UnionWith(SingleSelectionMembers());
        }

        if (members.All(chosenProcesses.Contains))
        {
            chosenProcesses.ExceptWith(members);
        }
        else
        {
            chosenProcesses.UnionWith(members);
        }

        selectedClusterKey = null;
        selectedGroupKey = null;
        chosenChannelKey = null;
        graphSelection = null;
        ForgetRelationship();
        if (selectedProcess is not null)
        {
            // The set replaces the single selection; the coordinator's cleared process does not clear the set.
            selectedProcess = null;
            OnPropertyChanged(nameof(SelectedProcess));
            selection.SelectProcess(null);
        }

        OnPropertyChanged(nameof(ChosenProcesses));
        OnPropertyChanged(nameof(HasMultiSelection));
        RaiseGraphSelectionChanged();
    }

    /// <summary>The processes the single selection stands for: an aggregate's, a group's, or the one process.</summary>
    private IEnumerable<ProcessInstanceId> SingleSelectionMembers() =>
        SelectedCluster is { } cluster ? cluster.Members
        : SelectedGroup is { } group ? wholeSnapshot.Processes
            .Where(process => string.Equals(process.GroupKey, group.Key, StringComparison.Ordinal))
            .Select(process => process.Id)
        : selectedProcess is { } process ? [process.Id]
        : [];

    /// <summary>Descends one rung, first recording on the rung being left the interval the user had there (§6.7).</summary>
    private bool TryDescend(LadderDescent descent)
    {
        ladder.RecordInterval(selectedInterval);
        return ladder.TryDescend(descent, out _);
    }

    /// <summary>
    /// Jumps to evidence in one step, from whichever rung the user is on (section 3.2). It lists what the inspector's
    /// evidence card counts: in a published session a chosen relationship's records at any rung; several processes chosen;
    /// a row chosen among a process's rows; a process selected at the machine rung or among a group's members; a group
    /// selected at the machine rung; and otherwise the rung's own - named in the filter bar, where it can be removed.
    /// </summary>
    public bool ShowEvidence()
    {
        if (ladder.Current.Level == DetailLevel.Evidence)
        {
            return false;
        }

        // Several processes chosen are what the card counts - choosing them lets a chosen relationship go, and choosing one
        // lets them go - and E lists them as Enter does (§6.7).
        if (HasMultiSelection)
        {
            return ShowChosenRecords();
        }

        // E lists the records behind the counts on screen, so it reads the same scope they answer (§6.4, I5): the button
        // that takes it stands beneath the card that counts the selection.
        TimeRange viewport = ScopeInterval ?? ladder.Current.Viewport;
        DetailLevel level = ladder.Current.Level;
        bool machine = realOverview && level == DetailLevel.Machine;
        string rung = NavigationState.Name(level).ToLowerInvariant();
        LadderDescent descent = realOverview && ChosenRelationshipRecords() is { } relationship
            ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport, relationship,
                $"Evidence was reached from the {rung} rung with this relationship chosen.")
            : ChosenRow is { } row
            ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport, row.Records,
                $"Evidence was reached from the {rung} rung with {(HttpExchangeKeys.IsHttp(row.Row.Key) ? "its" : "this")} {ChosenRowNoun(row.Row)} chosen.")
            : realOverview && level is DetailLevel.Machine or DetailLevel.Group && selectedProcess is { } process
            ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                new(DetailLevel.ProcessInstance, process.Id.ToString(), process.NameWithPid),
                $"Evidence was reached from the {rung} rung with this process selected.")
            : machine && SelectedGroup is { } group
                ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                    new(DetailLevel.Group, group.Key, group.Name),
                    "Evidence was reached from the machine rung with this executable group selected.")
                : LadderProjection.EvidenceDescentFor(ladder.Current, viewport);
        if (!TryDescend(descent))
        {
            return false;
        }

        AfterNavigation();
        return true;
    }

    /// <summary>
    /// Opens the evidence rung for a channel chosen outside the ladder, such as the discovery list above the overview's
    /// channel bound. The channel becomes the rung's visible scope filter.
    /// </summary>
    public bool ShowChannelEvidence(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!realOverview || ladder.Current.Level == DetailLevel.Evidence)
        {
            return false;
        }

        LadderDescent descent = LadderProjection.EvidenceDescentFor(ladder.Current,
            ScopeInterval ?? ladder.Current.Viewport,
            new(DetailLevel.Channel, channel.Key, channel.Name),
            "Evidence was reached from a channel chosen in the channel list.");
        if (!TryDescend(descent))
        {
            return false;
        }

        AfterNavigation();
        return true;
    }

    /// <summary>Ascends to exactly where the user was, or clears the selection at the machine rung.</summary>
    public bool Ascend()
    {
        ladder.RecordInterval(selectedInterval);
        NavigationState left = ladder.Current;
        if (!ladder.TryAscend(out NavigationState restored))
        {
            ClearSelection();
            return false;
        }

        ResumeIntervalOf(restored);
        ArriveFrom(left);

        // The row the rung was opened from is selected again, so the way back lands where the way down began and the
        // keyboard can take the next row from there (§3.2).
        if (!IsEvidenceRung && left.Focus is { } opened
            && RungRows.FirstOrDefault(row => string.Equals(row.Key, opened.Key, StringComparison.Ordinal)) is { } row)
        {
            SelectedRung = row;
        }

        return true;
    }

    /// <summary>Returns to a rung the breadcrumb names, exactly as the user left it, as an ascent does.</summary>
    public void ReturnTo(int depth)
    {
        ladder.RecordInterval(selectedInterval);
        NavigationState left = ladder.Current;
        if (ladder.TryReturnTo(depth, out NavigationState restored))
        {
            ResumeIntervalOf(restored);
            ArriveFrom(left);
        }
    }

    /// <summary>
    /// Shows the rung the ladder climbed to from <paramref name="left"/>. Climbing from a call's records to its channel,
    /// the channel's calls are read through that call, which is selected once read, however far down the channel's pages
    /// it lies: its rows arrive after this returns, where a rung's other rows are here now (§3.2).
    /// </summary>
    private void ArriveFrom(NavigationState left)
    {
        revealCall = left.Focus?.Key;
        try
        {
            AfterNavigation();
        }
        finally
        {
            revealCall = null;
        }
    }

    /// <summary>
    /// Alt+Right (§6.7): re-enters the rung the latest ascent or crumb left, as it was left, with the interval the user
    /// had there. A rung this generation no longer has ends forward history instead of landing somewhere else.
    /// </summary>
    public bool GoForward()
    {
        if (ladder.Forward is not [NavigationState next, ..])
        {
            return false;
        }

        if (!Reachable(next, ladder.Current, wholeSnapshot))
        {
            ladder.ClearForward();
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(ForwardLabel));
            OnPropertyChanged(nameof(DescendHint));
            return false;
        }

        ladder.RecordInterval(selectedInterval);
        if (!ladder.TryGoForward(out NavigationState entered))
        {
            return false;
        }

        ResumeIntervalOf(entered);
        AfterNavigation();
        return true;
    }

    /// <summary>
    /// Brings back the analysis interval a rung had when it was left, through the one selection source, so the brush,
    /// the ranking's counts and the timeline all name the same time (§6.4, §6.7).
    /// </summary>
    private void ResumeIntervalOf(NavigationState rung)
    {
        if (selection.Current.Interval != rung.IntervalWhenLeft)
        {
            selection.SelectInterval(rung.IntervalWhenLeft);
        }
    }

    public bool RemoveSelectedFilter()
    {
        if (selectedFilter is null || !ladder.TryRemoveFilter(selectedFilter.Field))
        {
            return false;
        }

        selectedFilter = null;
        AfterNavigation();
        return true;
    }

    /// <summary>
    /// Chooses a process, as a click on its node does. Chosen again while a row is described - its own rung's process,
    /// with one of its channels chosen - it is the selection again, so the card counts it and E lists its records.
    /// </summary>
    public void SelectProcess(ProcessInstanceId processId)
    {
        ProcessNode? process = Snapshot.Processes.FirstOrDefault(candidate => candidate.Id == processId);
        if (process is null)
        {
            return;
        }

        if (describesRow && selectedProcess?.Id == processId)
        {
            describesRow = false;
            RaiseGraphSelectionChanged();
            return;
        }

        SelectedProcess = process;
    }

    public void SelectInterval(TimeRange interval) => selection.SelectInterval(interval);

    public void ClearSelection()
    {
        selectedClusterKey = null;
        selectedGroupKey = null;
        chosenChannelKey = null;
        chosenProcesses.Clear();
        graphSelection = null;
        ForgetRelationship();
        selection.Clear();
        RaiseGraphSelectionChanged();
    }

    private bool TryResolveProcess(string key, out ProcessNode? process)
    {
        process = Snapshot.Processes.FirstOrDefault(
            candidate => string.Equals(candidate.Id.ToString(), key, StringComparison.Ordinal));
        return process is not null;
    }

    private void AfterNavigation()
    {
        view = ProjectLadder();
        selectedRung = null;
        selectedCrumb = null;

        // A group selected by its row is left with that row; a descent into it makes it the ladder's focus instead.
        selectedGroupKey = null;
        chosenChannelKey = null;
        chosenProcesses.Clear();
        graphSelection = null;
        RefreshGraphDisplay();

        // The relationship table lists what the rung's graph draws; a relationship chosen at the rung left goes with it.
        ForgetRelationship();
        relationships = RelationshipRows();
        OnPropertyChanged(nameof(Relationships));
        OnPropertyChanged(nameof(RelationshipTableScope));
        UpdateTimelineFocus();
        UpdateHighlight();
        RefreshIntervalRows(timelineFocusBuckets);

        // The machine rung's lanes and a group's plot bytes; another rung's count records, and its caption says so.
        FollowTimelineBytes();
        SyncEvidence();
        SyncRpc();
        SyncHttp();
        SyncConnections();
        RaiseRankingChanged();
        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(SelectedRung));
        OnPropertyChanged(nameof(Crumbs));
        OnPropertyChanged(nameof(SelectedCrumb));
        OnPropertyChanged(nameof(Filters));
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(LevelBadge));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(IsEmptyRung));
        OnPropertyChanged(nameof(ShowsRankedTable));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(CanAscend));
        OnPropertyChanged(nameof(AscendLabel));
        OnPropertyChanged(nameof(AscendDetail));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ForwardLabel));
        OnPropertyChanged(nameof(DescendHint));
        OnPropertyChanged(nameof(IntervalLabel));
        OnPropertyChanged(nameof(ShowsMechanismLanes));
        OnPropertyChanged(nameof(ShowsProcessLanes));
        OnPropertyChanged(nameof(ShowsDirectionLanes));
        OnPropertyChanged(nameof(ShowsChannelEndLanes));
        OnPropertyChanged(nameof(HasSelectedProcessLane));
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(OffersEvidenceStep));
        OnPropertyChanged(nameof(HighlightedEdgeKey));
        RaiseEvidenceChanged();
        describesRow = false;
        RaiseSelectionDescribed();

        // A channel rung states its channel's bytes, which are read when no selection has read them yet.
        FollowDescribedBytes();
    }

    /// <summary>Whether the user is at a published session's evidence rung, where rows are read from the session.</summary>
    public bool IsEvidenceRung => evidenceSource is not null && ladder.Current.Level == DetailLevel.Evidence;

    /// <summary>
    /// While the user inspects records the workspace keeps showing the generation they were read from, so rows,
    /// selection and marks stay still; recording continues and a newer generation is offered instead of applied.
    /// </summary>
    public bool HoldsGeneration => IsEvidenceRung;

    /// <summary>Completes when the most recent evidence page request has applied or reported a problem.</summary>
    public Task EvidenceReady { get; private set; } = Task.CompletedTask;

    public bool CanLoadMoreEvidence => evidence is { Loading: false, Problem: null, NextCursor: not null };

    public bool HasSelectedEvidence => IsEvidenceRung && selectedEvidence is not null;

    /// <summary>Whether the selected record has content the session keeps, which C opens in the content viewer (§3.7).</summary>
    public bool HasSelectedContent => SelectedEvidence?.Content is not null;

    public SessionEvidenceRecord? SelectedEvidence => IsEvidenceRung ? selectedEvidence : null;

    /// <summary>What the evidence rung reads, in words: the channel, the owners or the whole session, and any time range.</summary>
    public string EvidenceScopeText => IsEvidenceRung ? evidence?.Scope.Description ?? string.Empty : string.Empty;

    /// <summary>How much of the scope is loaded, and whether a page continued in a newer generation.</summary>
    public string EvidenceStatus
    {
        get
        {
            if (evidence is not { } list) return string.Empty;
            if (list.Problem is not null) return "Source records unavailable";
            string loaded = string.Create(CultureInfo.CurrentCulture, $"{list.Records.Count:N0} source records");
            string state = list.Loading
                ? list.Records.Count == 0 ? "Reading source records…" : loaded + " · reading more…"
                : list.NextCursor is null ? loaded + " · end of scope" : loaded + " · more available (M)";
            return list.ContinuedIn is { } generation
                ? state + string.Create(CultureInfo.CurrentCulture, $" · later pages read generation {generation:N0}")
                : state;
        }
    }

    /// <summary>
    /// The record action's label. A redacted package holds synthetic records only, and its action never promises the
    /// original that was left out.
    /// </summary>
    public string OriginalRecordLabel => wholeSnapshot.Redaction is null
        ? "Open original record (Enter)"
        : "Open synthetic record (Enter)";

    /// <summary>What Enter opens at the evidence rung, in words the rail, its hint and a screen reader share.</summary>
    private string RecordNoun => wholeSnapshot.Redaction is null ? "original record" : "synthetic record";

    public string SelectedEvidenceTitle => SelectedEvidence is { } record
        ? EvidenceRowText.Title(record.Observation)
            + (EvidenceRowText.Size(record.Observation, CultureInfo.CurrentCulture) is { } size ? " · " + size : string.Empty)
        : "No record selected";

    /// <summary>The selected record's facts, labelled, as the inspector shows them.</summary>
    public IReadOnlyList<EvidenceField> SelectedEvidenceFields
    {
        get
        {
            if (SelectedEvidence is not { } record) return [];
            ObservationRowV1 row = record.Observation;
            var fields = new List<EvidenceField>
            {
                new("When", EvidenceRowText.When(row, CultureInfo.CurrentCulture)),
                new("Owner", EvidenceRowText.Owner(record, CultureInfo.CurrentCulture)),
            };
            if (EvidenceRowText.Endpoints(row) is { } endpoints) fields.Add(new("Endpoints", endpoints));
            if (EvidenceRowText.SizeWithDomain(row, CultureInfo.CurrentCulture) is { } size) fields.Add(new("Size", size));

            // Whether the bytes or arguments themselves were kept, and why not: the question a transfer's size raises (§3.7).
            fields.Add(new("Content", RecordContent.Of(row, synthetic: wholeSnapshot.Redaction is not null, record.Content,
                record.ContentProblem).Describe()));
            fields.Add(new("Source", string.Create(CultureInfo.InvariantCulture,
                $"{EvidenceRowText.ProviderName(row.ProviderId, wholeSnapshot.Redaction is not null)} · event {row.EventId} v{row.DescriptorVersion}")));
            fields.Add(new("Quality", EvidenceRowText.Quality(row)));
            fields.Add(new("Record", string.Create(CultureInfo.InvariantCulture,
                $"raw {row.RawStreamId}/{row.RawSourceEpoch}/{row.RawRecordOrdinal} · {record.SegmentName} row {record.SegmentRow}")));
            return fields;
        }
    }

    /// <summary>Reading times of the loaded records, in workspace ticks, drawn as individual marks on the timeline.</summary>
    public IReadOnlyList<long> EvidenceMarkTicks => IsEvidenceRung && evidence is { } list
        ? [.. list.Records.Where(record => record.Observation.SessionRelativeTicks is not null)
            .Select(record => record.Observation.SessionRelativeTicks!.Value / 100)]
        : [];

    public long? SelectedEvidenceTick => SelectedEvidence?.Observation.SessionRelativeTicks is { } nanoseconds
        ? nanoseconds / 100
        : null;

    /// <summary>
    /// [ and ] at the evidence rung (§6.7): selects the previous or next loaded record in reading order, or the first or
    /// last when none is selected. False at either end of what is loaded; "Load more" continues the scope.
    /// </summary>
    public bool StepEvidence(int direction)
    {
        if (!IsEvidenceRung || direction == 0)
        {
            return false;
        }

        IReadOnlyList<RungRow> rows = RungRows;
        int index = -1;
        for (int i = 0; selectedRung is not null && i < rows.Count; i++)
        {
            if (rows[i].Key == selectedRung.Key)
            {
                index = i;
                break;
            }
        }

        int next = index < 0 ? (direction > 0 ? 0 : rows.Count - 1) : index + Math.Sign(direction);
        if (next < 0 || next >= rows.Count)
        {
            return false;
        }

        SelectedRung = rows[next];
        return true;
    }

    /// <summary>
    /// The graph edge that contributes to what the rung shows: the focused channel's edge at the channel rung, and the
    /// scope's channel at the evidence rung. Null when the rung is not one channel.
    /// </summary>
    public string? HighlightedEdgeKey
    {
        get
        {
            if (IsEvidenceRung) return evidence?.Scope.ContributingEdgeKey;

            // Elsewhere than a channel's rung, the relationship chosen in the table or by a click on its edge is haloed.
            if (ladder.Current.Level != DetailLevel.Channel || ladder.Current.Focus is not { } focus) return selectedRelationship?.Key;

            // The graph reads this on every repaint of a channel rung, so it is a plain scan rather than a query (R11).
            IReadOnlyList<Channel> channels = Snapshot.Channels;
            for (int index = 0; index < channels.Count; index++)
            {
                if (channels[index].Key == focus.Key)
                {
                    return channels[index].EdgeKey;
                }
            }

            return null;
        }
    }

    /// <summary>Reads the next page of the evidence rung's scope, continuing after the last record shown.</summary>
    public Task LoadMoreEvidenceAsync()
    {
        if (evidence is not { Loading: false, Problem: null, NextCursor: { } cursor } list)
        {
            return Task.CompletedTask;
        }

        EvidenceReady = LoadEvidenceAsync(list, cursor);
        return EvidenceReady;
    }

    private void SyncEvidence()
    {
        if (!IsEvidenceRung)
        {
            CancelEvidence();
            return;
        }

        EvidenceScope scope = EvidenceScopes.Resolve(Snapshot, ladder.Current);
        if (evidence is { } current && SameScope(current.Scope, scope))
        {
            return;
        }

        CancelEvidence();
        var list = new EvidenceList(scope);
        evidence = list;
        if (scope.Problem is { } problem)
        {
            list.Problem = problem;
            RebuildEvidenceRows();
            return;
        }

        EvidenceReady = LoadEvidenceAsync(list, null);
    }

    private async Task LoadEvidenceAsync(EvidenceList list, string? cursor)
    {
        SessionEvidenceSource source = evidenceSource!;
        list.Loading = true;
        RaiseEvidenceChanged();
        try
        {
            SessionEvidencePage page = await source.ReadAsync(list.Scope, cursor, list.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(evidence, list))
            {
                return;
            }

            if (page.SessionId != source.SessionId)
            {
                list.Problem = "This directory now holds another session. Open it again to read its records.";
                list.NextCursor = null;
                return;
            }

            if (page.RestartRequired)
            {
                list.Problem = page.RestartReason;
                list.NextCursor = null;
                return;
            }

            list.Records.AddRange(page.Records);
            list.NextCursor = page.NextCursor;
            if (page.Generation != source.Generation)
            {
                list.ContinuedIn = page.Generation;
            }
        }
        catch (OperationCanceledException) when (list.Cancellation.IsCancellationRequested)
        {
            // Leaving the rung cancels its read; nothing is applied.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (ReferenceEquals(evidence, list))
            {
                list.Problem = exception.Message;
                list.NextCursor = null;
            }
        }
        finally
        {
            list.Loading = false;
            if (!disposed && ReferenceEquals(evidence, list))
            {
                RebuildEvidenceRows();
                if (pendingEvidenceKey is { } key && evidenceRows.FirstOrDefault(row => row.Key == key) is { } row)
                {
                    pendingEvidenceKey = null;
                    SelectedRung = row;
                }

                RaiseEvidenceChanged();
            }
        }
    }

    private void SelectEvidence(string? key)
    {
        selectedEvidence = key is null || evidence is null
            ? null
            : evidence.Records.FirstOrDefault(record => EvidenceKey(record) == key);
        OnPropertyChanged(nameof(SelectedEvidence));
        OnPropertyChanged(nameof(HasSelectedEvidence));
        OnPropertyChanged(nameof(HasSelectedContent));
        OnPropertyChanged(nameof(SelectedEvidenceTitle));
        OnPropertyChanged(nameof(SelectedEvidenceFields));
        OnPropertyChanged(nameof(SelectedEvidenceTick));
    }

    private void CancelEvidence()
    {
        if (evidence is { } list)
        {
            list.Cancellation.Cancel();
            list.Cancellation.Dispose();
        }

        evidence = null;
        selectedEvidence = null;
        evidenceRows = [];
    }

    private void RebuildEvidenceRows()
    {
        if (evidence is not { } list)
        {
            evidenceRows = [];
            return;
        }

        evidenceRows = [.. list.Records.Select(record => EvidenceRow(record, RecordNoun))];
    }

    private static RungRow EvidenceRow(SessionEvidenceRecord record, string recordNoun)
    {
        ObservationRowV1 row = record.Observation;
        FamilyTokens tokens = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(row.Mechanism));
        string title = EvidenceRowText.Title(row);
        string? size = EvidenceRowText.Size(row, CultureInfo.CurrentCulture);
        string ownership = EvidenceRowText.Ownership(record, CultureInfo.CurrentCulture);
        string when = EvidenceRowText.When(row, CultureInfo.CurrentCulture);
        string pid = EvidenceRowText.OwnerProcessId(row) is { } id ? string.Create(CultureInfo.CurrentCulture, $"PID {id}") : "no owner";
        // The rail is narrow: what happened and its size on the first line, when and whose on the second. Endpoints
        // and the full owner are the inspector's, where they fit without being cut.
        string detail = when + " · " + pid;
        var source = new LadderRow(EvidenceKey(record), title, detail, 1, row.ByteValue, row.Mechanism,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
        string endpoints = EvidenceRowText.Endpoints(row) is { } pair ? ", " + pair : string.Empty;
        return new(EvidenceKey(record), size is null ? title : title + " · " + size, detail, string.Empty,
            size ?? string.Empty, tokens.Label, tokens.Glyph, string.Empty, recordNoun, source)
        {
            SpokenName = $"{title}{(size is null ? string.Empty : ", " + size)}, at {when}{endpoints}, {ownership}. "
                + $"Press Enter to open the {recordNoun}."
                + (record.Content is null ? string.Empty : " Press C to inspect its content."),
        };
    }

    private static string EvidenceKey(SessionEvidenceRecord record) => string.Create(CultureInfo.InvariantCulture,
        $"{record.ObservationId.RawRecordId.StreamId}/{record.ObservationId.RawRecordId.SourceEpoch}/"
        + $"{record.ObservationId.RawRecordId.RecordOrdinal}/{record.ObservationId.FactKey}");

    private static bool SameScope(EvidenceScope left, EvidenceScope right) =>
        left.ChannelKey == right.ChannelKey
        && left.Interval == right.Interval
        && left.Problem == right.Problem
        && left.OwnerProcesses.SequenceEqual(right.OwnerProcesses);

    private void RaiseEvidenceChanged()
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsEvidenceRung));
        OnPropertyChanged(nameof(HoldsGeneration));
        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(IsEmptyRung));
        OnPropertyChanged(nameof(ShowsRankedTable));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(EvidenceStatus));
        OnPropertyChanged(nameof(EvidenceScopeText));
        OnPropertyChanged(nameof(CanLoadMoreEvidence));
        OnPropertyChanged(nameof(ShowsLoadMoreEvidence));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(ShowsLoadMore));
        OnPropertyChanged(nameof(LoadMoreLabel));
        OnPropertyChanged(nameof(LoadMoreName));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(EvidenceMarkTicks));
        OnPropertyChanged(nameof(HighlightedEdgeKey));
        OnPropertyChanged(nameof(SelectedEvidence));
        OnPropertyChanged(nameof(HasSelectedEvidence));
        OnPropertyChanged(nameof(HasSelectedContent));
        OnPropertyChanged(nameof(SelectedEvidenceTitle));
        OnPropertyChanged(nameof(SelectedEvidenceFields));
        OnPropertyChanged(nameof(SelectedEvidenceTick));
    }

    /// <summary>Completes when the latest read of the call lane's calls has applied or reported a problem.</summary>
    public Task RpcSpansReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The RPC channel rung's calls within the drawn viewport, in reading order; null where no call lane is drawn, and empty
    /// where the view holds more calls than the lane draws one by one and <see cref="RpcCallDensity"/> draws them instead.
    /// </summary>
    public IReadOnlyList<RpcCallSpanView>? RpcCallSpans => ShowsRpcCallLane ? rpcSpans!.Calls : null;

    /// <summary>
    /// The call lane's density columns (§6.2), when the viewport holds more calls than the lane draws one by one; null
    /// otherwise.
    /// </summary>
    public RpcCallDensity? RpcCallDensity => ShowsRpcCallLane ? rpcSpans!.Density : null;

    /// <summary>
    /// How many density columns the call lane asks for: twice the timeline's detail columns, about five logical pixels
    /// each, which is §6.2's minimum drawn width, so every column is a pointer target without widening.
    /// </summary>
    private int CallDensityColumns => Math.Clamp((drawnTimeline?.Columns ?? 128) * 2, 32, 512);

    /// <summary>Whether the timeline draws the RPC channel's calls as a lane of duration bars under the machine row.</summary>
    public bool ShowsRpcCallLane => IsRpcChannelRung && rpcSpans is { Problem: null };

    /// <summary>
    /// What the call lane holds, in one short line: every call in view, drawn one by one or as density. The timeline states
    /// it on every repaint, so it is formatted once per read of the lane, where it was once formatted on every frame (R11).
    /// </summary>
    public string RpcCallLaneNote
    {
        get
        {
            if (rpcSpans is not { Problem: null } spans || !IsRpcChannelRung)
            {
                return string.Empty;
            }

            if (!ReferenceEquals(rpcCallLaneNote.Page, spans))
            {
                rpcCallLaneNote = (spans, OperationLaneNote(spans.Total, spans.Density is not null));
            }

            return rpcCallLaneNote.Text;
        }
    }

    private (object? Page, string Text) rpcCallLaneNote = (null, string.Empty);

    /// <summary>An operation lane's note: how many calls or exchanges are in view, and whether they are drawn as density.</summary>
    private static string OperationLaneNote(long total, bool density) => density
        ? string.Create(CultureInfo.CurrentCulture, $"{total:N0} · density")
        : string.Create(CultureInfo.CurrentCulture, $"{total:N0} in view");

    /// <summary>The call selected in the RPC channel rung's table, which the call lane outlines.</summary>
    public string? SelectedRpcCallKey => IsRpcChannelRung ? selectedRung?.Key : null;

    /// <summary>Selects a call the lane drew, when its row is among those listed; false when it is not.</summary>
    public bool SelectRpcCall(string key)
    {
        if (!IsRpcChannelRung || RungRows.FirstOrDefault(row => row.Key == key) is not { } row)
        {
            return false;
        }

        SelectedRung = row;
        return true;
    }

    /// <summary>What a call the lane draws is, for its hover card.</summary>
    public HoverCard DescribeRpcCallHover(RpcCallSpanView span)
    {
        ArgumentNullException.ThrowIfNull(span);
        string title = span.State == RpcCallState.Completed && span.StartTicks is { } start && span.EndTicks is { } end
            ? "RPC call · " + OperationText.Duration((end - start) * 100, CultureInfo.CurrentCulture)
            : "RPC call · " + OperationText.State(span.State);
        var lines = new List<string>
        {
            span.Status is { } status
                ? status == 0 ? "Succeeded" : string.Create(CultureInfo.CurrentCulture, $"Failed, status {status:N0}")
                : "No status",
            span.StartTicks is { } began
                ? "Started " + WorkspaceTime.FormatInstant(began, Math.Max(1, (span.EndTicks ?? began) - began + 1), CultureInfo.CurrentCulture)
                : "Its start is not in the evidence",
        };
        if (span.Procedure is { } procedure) lines.Add(string.Create(CultureInfo.CurrentCulture, $"Procedure {procedure:N0}"));
        lines.Add(RungRows.Any(row => row.Key == span.Key) ? "Click selects its row" : "Load more calls to select its row");
        return new(title, lines);
    }

    /// <summary>What one density column of the call lane holds, for its hover card.</summary>
    public HoverCard DescribeRpcDensityHover(int column)
    {
        RpcCallDensity density = RpcCallDensity ?? throw new InvalidOperationException("The call lane draws no density.");
        TimeRange interval = density.ColumnInterval(column);
        long running = density.Running[column];
        long failed = density.Failed[column];
        string title = running == 0
            ? "No call running"
            : string.Create(CultureInfo.CurrentCulture, $"{running:N0} {(running == 1 ? "call" : "calls")} running");
        var lines = new List<string>();
        if (failed > 0)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture, $"{failed:N0} of them failed"));
        }

        lines.Add("From " + WorkspaceTime.FormatInstant(interval.StartTicks, interval.SpanTicks, CultureInfo.CurrentCulture)
            + " for " + WorkspaceTime.FormatDuration(interval.SpanTicks, CultureInfo.CurrentCulture));
        lines.Add(string.Create(CultureInfo.CurrentCulture, $"The busiest column holds {density.Maximum:N0}; a call counts in every column it ran in"));
        lines.Add("Click selects this interval; zoom in to see each call");
        return new(title, lines);
    }

    /// <summary>
    /// Reads the calls the lane draws for <paramref name="viewport"/> at an RPC channel rung, superseding a read still
    /// under way; any other rung lets the lane go.
    /// </summary>
    private void RequestRpcSpans(TimeRange viewport)
    {
        if (disposed)
        {
            return;
        }

        if (!IsRpcChannelRung || evidenceSource is null)
        {
            if (rpcSpans is not null || requestedRpcSpans is not null)
            {
                CancelRpcSpans();
                RaiseRpcSpansChanged();
            }

            return;
        }

        string key = ladder.Current.Focus?.Key ?? string.Empty;
        int columns = CallDensityColumns;
        if (requestedRpcSpans == (key, viewport, columns))
        {
            return;
        }

        requestedRpcSpans = (key, viewport, columns);
        rpcSpanQuery?.Cancel();
        rpcSpanQuery?.Dispose();
        var query = new CancellationTokenSource();
        rpcSpanQuery = query;
        RpcSpansReady = LoadRpcSpansAsync(evidenceSource, key, viewport, columns, query);
    }

    private async Task LoadRpcSpansAsync(
        SessionEvidenceSource source, string key, TimeRange viewport, int columns, CancellationTokenSource query)
    {
        try
        {
            RpcCallSpanPage page = await source.RpcSpansAsync(key, viewport, columns, query.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(rpcSpanQuery, query)) return;
            rpcSpans = page;
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(rpcSpanQuery, query)) return;
            rpcSpans = new(Guid.Empty, 0, viewport, [], 0, exception.Message);
        }

        RaiseRpcSpansChanged();
    }

    private void CancelRpcSpans()
    {
        rpcSpanQuery?.Cancel();
        rpcSpanQuery?.Dispose();
        rpcSpanQuery = null;
        requestedRpcSpans = null;
        rpcSpans = null;
    }

    private void RaiseRpcSpansChanged()
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(RpcCallSpans));
        OnPropertyChanged(nameof(RpcCallDensity));
        OnPropertyChanged(nameof(ShowsRpcCallLane));
        OnPropertyChanged(nameof(RpcCallLaneNote));
        OnPropertyChanged(nameof(TimelineCaption));
    }

    /// <summary>Whether the user is at a published session's RPC channel rung, whose rows are its calls.</summary>
    public bool IsRpcChannelRung => evidenceSource is not null
        && ladder.Current.Level == DetailLevel.Channel
        && RpcChannelKeys.IsRpc(ladder.Current.Focus?.Key);

    /// <summary>Completes when the latest read of RPC channels or calls has applied or reported a problem.</summary>
    public Task RpcReady { get; private set; } = Task.CompletedTask;

    public bool CanLoadMoreCalls => IsRpcChannelRung && rpcCalls is { Loading: false, Problem: null, More: true };

    /// <summary>Reads the RPC channel rung's next page of calls, after the last call shown.</summary>
    public Task LoadMoreCallsAsync()
    {
        if (!CanLoadMoreCalls)
        {
            return Task.CompletedTask;
        }

        RpcReady = LoadRpcCallsAsync(rpcCalls!);
        return RpcReady;
    }

    /// <summary>
    /// What an RPC channel rung states: its calls, how they ended and how long they took, and that its records are one
    /// step away.
    /// </summary>
    private string RpcCallSummary(bool brief)
    {
        if (rpcCalls is not { } load) return string.Empty;
        if (load.Problem is not null) return "Calls unavailable";
        if (load.Channel is not { } channel) return "Reading calls…";
        string loaded = load.Calls.Count < channel.Counts.Calls
            ? string.Create(CultureInfo.CurrentCulture, $" · {load.Calls.Count:N0} listed")
            : string.Empty;
        return brief
            ? channel.Outcome(CultureInfo.CurrentCulture) + loaded
            : channel.Outcome(CultureInfo.CurrentCulture) + loaded
                + " · paired start to stop by activity id; E shows the records";
    }

    /// <summary>The key when it names a call on <paramref name="channelKey"/>; null for any other key.</summary>
    private static string? CallOn(string channelKey, string? key) =>
        RpcChannelKeys.TryParseCall(key, out string channel, out _) && string.Equals(channel, channelKey, StringComparison.Ordinal)
            ? key
            : null;

    /// <summary>A row for an RPC channel: one process's calls on one side to one interface, counted in call records.</summary>
    private static LadderRow RpcChannelRow(string key, string label, string detail, long records) =>
        new(key, label, detail, records, null, Mechanism.Rpc, CoverageState.UnknownCoverage, DetailLevel.Channel,
            AccountingSide.CanonicalOwner);

    /// <summary>
    /// Starts the reads the rung needs: a process's RPC channels when it made RPC calls, and an RPC channel's first page of
    /// calls. Any other rung lets both go.
    /// </summary>
    private void SyncRpc()
    {
        if (IsRpcChannelRung)
        {
            string key = ladder.Current.Focus?.Key ?? string.Empty;
            CancelRpcChannels();
            if (rpcCalls is { } current && current.ChannelKey == key && current.Scope == CountedScope)
            {
                return;
            }

            CancelRpcCalls();
            var calls = new RpcCallsLoad(key, CountedScope) { Reveal = CallOn(key, revealCall) };
            rpcCalls = calls;
            RpcReady = LoadRpcCallsAsync(calls);
            RequestRpcSpans(drawnTimeline?.Viewport ?? ladder.Current.Viewport);
            return;
        }

        CancelRpcCalls();
        RequestRpcSpans(ladder.Current.Viewport);
        // Whether the process has RPC channels at all is the whole session's fact: a brush in which it made no call keeps
        // its channels, counting none, as its paired channels do.
        if (evidenceSource is not null
            && ladder.Current.Level == DetailLevel.ProcessInstance
            && ladder.Current.Focus is { } focus
            && wholeSnapshot.Processes.FirstOrDefault(process => process.Id.ToString() == focus.Key) is { } process
            && process.Activity.Any(count => count.Mechanism == Mechanism.Rpc && count.Records > 0))
        {
            if (rpcChannels is { } known && known.Instance == process.Id && known.Scope == CountedScope)
            {
                return;
            }

            // The previous scope's rows stay on screen until this scope's are read, rather than blinking empty (§6.4).
            IReadOnlyList<RpcChannelSummary> previous = rpcChannels is { } shown && shown.Instance == process.Id ? shown.Channels : [];
            CancelRpcChannels();
            var channels = new RpcChannelsLoad(process.Id, CountedScope) { Channels = previous };
            rpcChannels = channels;
            RebuildRpcRows();
            RpcReady = LoadRpcChannelsAsync(channels);
            return;
        }

        CancelRpcChannels();
    }

    /// <summary>
    /// Re-reads a process's RPC channels, or a channel's calls, when the scope the rung counts changed - a brush or the
    /// visible range - so they count what the rows beside them count (§6.4). The previous scope's rows stay on screen until
    /// the new scope's first page replaces them.
    /// </summary>
    private void SyncRpcScope()
    {
        if (IsRpcChannelRung && rpcCalls is { } calls && calls.Scope != CountedScope)
        {
            // The selected call stays listed, and selected, when the new scope holds it, however far down it is.
            var reload = new RpcCallsLoad(calls.ChannelKey, CountedScope)
            {
                Channel = calls.Channel,
                ReplaceOnFirstPage = true,
                Reveal = CallOn(calls.ChannelKey, selectedRung?.Key),
            };
            reload.Calls.AddRange(calls.Calls);
            calls.Cancellation.Cancel();
            calls.Cancellation.Dispose();
            rpcCalls = reload;
            RebuildRpcRows();
            RpcReady = LoadRpcCallsAsync(reload);
        }
        else if (!IsRpcChannelRung && ladder.Current.Level == DetailLevel.ProcessInstance
            && rpcChannels is { } channels && channels.Scope != CountedScope)
        {
            SyncRpc();
        }
    }

    private async Task LoadRpcChannelsAsync(RpcChannelsLoad load)
    {
        load.Loading = true;
        RaiseRpcChanged();
        try
        {
            RpcChannelList list = await evidenceSource!.RpcChannelsAsync(load.Instance, load.Scope, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(rpcChannels, load)) return;
            load.Channels = list.Channels;
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(rpcChannels, load)) return;
            load.Problem = "This process's RPC calls could not be read: " + exception.Message;
        }
        finally
        {
            load.Loading = false;
        }

        RebuildRpcRows();
    }

    private async Task LoadRpcCallsAsync(RpcCallsLoad load)
    {
        load.Loading = true;
        RaiseRpcChanged();
        int offset = load.ReplaceOnFirstPage ? 0 : load.Calls.Count;
        string? reveal = offset == 0 ? load.Reveal : null;
        load.Reveal = null;
        try
        {
            RpcCallPage page = reveal is null
                ? await evidenceSource!.RpcCallsAsync(load.ChannelKey, offset, load.Scope, load.Cancellation.Token)
                    .AnsweredLater()
                : await evidenceSource!.RpcCallsThroughAsync(reveal, load.Scope, load.Cancellation.Token)
                    .AnsweredLater();
            if (disposed || !ReferenceEquals(rpcCalls, load)) return;
            if (page.Problem is not null)
            {
                load.Problem = page.Problem;
                load.More = false;
            }
            else
            {
                load.Channel = page.Channel;
                if (load.ReplaceOnFirstPage)
                {
                    load.Calls.Clear();
                    load.ReplaceOnFirstPage = false;
                }

                load.Calls.AddRange(page.Calls);
                load.More = page.More;
            }
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(rpcCalls, load)) return;
            load.Problem = "This channel's calls could not be read: " + exception.Message;
            load.More = false;
        }
        finally
        {
            load.Loading = false;
        }

        RebuildRpcRows();

        // The call the user came back up from is selected once listed, so the way back lands where the way down began
        // (§3.2); a row the user selected meanwhile stays selected.
        if (reveal is not null && selectedRung is null
            && RungRows.FirstOrDefault(row => string.Equals(row.Key, reveal, StringComparison.Ordinal)) is { } revealed)
        {
            SelectedRung = revealed;
        }
    }

    /// <summary>Rebuilds the rows the RPC reads supply, keeping the selected row when it is still listed.</summary>
    private void RebuildRpcRows()
    {
        if (disposed)
        {
            return;
        }

        ThemeMode mode = ThemeResources.CurrentMode;
        FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(Mechanism.Rpc));
        rpcChannelRows = rpcChannels is { } channels
            ? [.. channels.Channels.Select(channel => RpcChannelRungRow(channel, tokens))]
            : [];
        rpcCallRows = rpcCalls is { Channel: { } summary } calls
            ? [.. calls.Calls.Select(call => RpcCallRungRow(call, summary, tokens))]
            : [];
        string? selectedKey = selectedRung?.Key;
        RaiseRpcChanged();
        if (selectedKey is not null && RungRows.FirstOrDefault(row => row.Key == selectedKey) is { } kept)
        {
            selectedRung = kept;
            OnPropertyChanged(nameof(SelectedRung));

            // The kept row is read for the new scope: the inspector describes it as it now reads.
            RaiseSelectionDescribed();
        }
    }

    /// <summary>
    /// An RPC channel's row. The rail is narrow, so it leads with the interface and states the side and the calls under
    /// it; the whole name is what its crumb, its filter and its records' scope say.
    /// </summary>
    private static RungRow RpcChannelRungRow(RpcChannelSummary channel, FamilyTokens tokens)
    {
        string label = channel.Interface is null ? "Calls whose start was not seen" : RpcInterfaceNames.Describe(channel.Interface);
        // Who is at the other end leads, once the capture collected ALPC to follow each call's message (ADR-034): the
        // rail is narrow, and in such a session that is what a channel's row is read for.
        string? peers = channel.Peers?.Describe(channel.Side, channel.Counts.Calls, CultureInfo.CurrentCulture);
        string detail = (channel.Side == RpcCallSide.Client ? "RPC client · " : "RPC server · ")
            + (peers is null ? string.Empty : peers + " · ")
            + channel.Outcome(CultureInfo.CurrentCulture);
        LadderRow source = RpcChannelRow(channel.Key, channel.Name, detail, channel.Records);
        return new(channel.Key, label, detail, channel.Records.ToString("N0", CultureInfo.CurrentCulture),
            WorkspaceRowBuilder.DescribeBytes(null), tokens.Label, tokens.Glyph, string.Empty,
            NavigationState.Name(DetailLevel.Channel), source)
        {
            SpokenName = $"{channel.Name}, {detail}, {Spoken.Count(channel.Records, "call record")}. RPC carries no "
                + "size. Press Enter to open the channel's calls.",
        };
    }

    private static RungRow RpcCallRungRow(RpcCallRow row, RpcChannelSummary channel, FamilyTokens tokens)
    {
        RpcCall call = row.Call;
        string when = (call.Start ?? call.Stop)!.SessionRelativeTicks is { } nanoseconds
            ? string.Create(CultureInfo.CurrentCulture, $"+{nanoseconds / 1_000_000_000m:0.000000} s")
            : "time unavailable";
        string procedure = call.Procedure is { } number
            ? string.Create(CultureInfo.CurrentCulture, $" · procedure {number:N0}")
            : string.Empty;

        // The row leads with how the call went, which is what a list of calls is scanned for; the rung names the channel.
        string label = (call.State == RpcCallState.Completed
            ? OperationText.Duration(call.DurationNanoseconds!.Value, CultureInfo.CurrentCulture)
            : OperationText.State(call.State)) + procedure;
        string status = call.Status is { } code
            ? code == 0 ? "succeeded" : string.Create(CultureInfo.CurrentCulture, $"failed, status {code:N0}")
            : "no status";
        string? otherEnd = row.OtherEnd?.Describe(call.Side);
        string detail = otherEnd is null ? $"{when} · {status}" : $"{when} · {status} · {otherEnd}";
        long records = (call.Start is null ? 0 : 1) + (call.Stop is null ? 0 : 1);
        var source = new LadderRow(row.Key, $"RPC call at {when}", detail, records, null, Mechanism.Rpc,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
        // A linked call opens the call at its other end, on that call's own channel (operations-v1 §5c).
        RpcCallPeerView? linked = row.OtherEnd is { CallKey: not null, Process: not null } other ? other : null;
        string? otherWhen = linked?.FirstNanoseconds is { } otherNanoseconds
            ? string.Create(CultureInfo.CurrentCulture, $"+{otherNanoseconds / 1_000_000_000m:0.000000} s")
            : null;
        return new(row.Key, label, detail, records.ToString("N0", CultureInfo.CurrentCulture),
            WorkspaceRowBuilder.DescribeBytes(null), tokens.Label, tokens.Glyph, string.Empty,
            NavigationState.Name(DetailLevel.Evidence), source)
        {
            SpokenName = $"RPC call at {when}, {label}, {status}{(otherEnd is null ? string.Empty : ", " + otherEnd)}, "
                + $"{channel.Name}. Press Enter to open its records"
                + (linked is null ? "." : ", or O to open the call at its other end."),
            OtherEndKey = linked?.CallKey,
            OtherEndLabel = linked is null ? null : otherWhen is null ? "RPC call" : $"RPC call at {otherWhen}",
            OtherEndCall = linked is null
                ? null
                : call.Side == RpcCallSide.Client
                    ? $"the call {linked.Process!.Name} served"
                    : $"the call {linked.Process!.Name} made",
        };
    }

    private void CancelRpc()
    {
        CancelRpcChannels();
        CancelRpcCalls();
        CancelRpcSpans();
    }

    private void CancelRpcChannels()
    {
        if (rpcChannels is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        rpcChannels = null;
        rpcChannelRows = [];
    }

    private void CancelRpcCalls()
    {
        if (rpcCalls is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        rpcCalls = null;
        rpcCallRows = [];
    }

    private void RaiseRpcChanged()
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(IsEmptyRung));
        OnPropertyChanged(nameof(ShowsRankedTable));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(OffersEvidenceStep));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(ShowsLoadMore));
        OnPropertyChanged(nameof(LoadMoreLabel));
        OnPropertyChanged(nameof(LoadMoreName));
    }

    /// <summary>One process's RPC channels as read for its rung.</summary>
    private sealed class RpcChannelsLoad(ProcessInstanceId instance, TimeRange? scope)
    {
        public ProcessInstanceId Instance { get; } = instance;

        /// <summary>The interval the channels count, or the whole session (null): the scope the rung's rows count.</summary>
        public TimeRange? Scope { get; } = scope;

        public IReadOnlyList<RpcChannelSummary> Channels { get; set; } = [];

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>One RPC channel's calls as read for its rung, a page at a time.</summary>
    private sealed class RpcCallsLoad(string channelKey, TimeRange? scope)
    {
        public string ChannelKey { get; } = channelKey;

        /// <summary>The interval the calls are listed in, or the whole session (null).</summary>
        public TimeRange? Scope { get; } = scope;

        /// <summary>Whether the calls shown are the previous scope's, which this scope's first page replaces.</summary>
        public bool ReplaceOnFirstPage { get; set; }

        /// <summary>
        /// The call the first read lists the calls through, and selects when nothing else is: the one the user came back
        /// up from, or the one selected when the scope changed.
        /// </summary>
        public string? Reveal { get; set; }

        public RpcChannelSummary? Channel { get; set; }

        public List<RpcCallRow> Calls { get; } = [];

        public bool More { get; set; }

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>The evidence rung's loaded rows for one scope. A new scope starts a new list and cancels the old read.</summary>
    private sealed class EvidenceList(EvidenceScope scope)
    {
        public EvidenceScope Scope { get; } = scope;

        public List<SessionEvidenceRecord> Records { get; } = [];

        public string? NextCursor { get; set; }

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        /// <summary>The newer generation a page was read from, when the list continued past the workspace's own.</summary>
        public long? ContinuedIn { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>
    /// Follows the analysis interval: a brushed interval re-ranks every rung by the records inside it, and clearing the
    /// brush returns to the whole session. A newer brush supersedes an older count still being read (R7).
    /// </summary>
    private void SyncIntervalScope()
    {
        if (evidenceSource is null || ScopeInterval == scopedInterval)
        {
            return;
        }

        intervalQuery?.Cancel();
        intervalQuery?.Dispose();
        intervalQuery = null;
        scopedInterval = ScopeInterval;
        intervalProblem = null;
        if (scopedInterval is not { } interval)
        {
            intervalLoading = false;
            ApplyScope(null);
            IntervalReady = Task.CompletedTask;
            return;
        }

        var query = new CancellationTokenSource();
        intervalQuery = query;
        IntervalReady = LoadIntervalAsync(evidenceSource, interval, query);
    }

    private async Task LoadIntervalAsync(SessionEvidenceSource source, TimeRange interval, CancellationTokenSource query)
    {
        intervalLoading = true;
        OnPropertyChanged(nameof(RankingScopeText));
        OnPropertyChanged(nameof(ShowsRankingScope));
        try
        {
            // A ranking's bytes or calls are read beside the counts and applied with them, so the rows change once.
            RankingFamily family = RankingMetrics.FamilyOf(rankBy);
            Task<IRankingMeasures?> measures = MeasuresBesideCountsAsync(source, interval);
            SessionIntervalCounts counts = await source.CountAsync(interval, query.Token).AnsweredLater();
            IRankingMeasures? measured = await measures.AnsweredLater();
            if (disposed || !ReferenceEquals(intervalQuery, query))
            {
                return;
            }

            intervalLoading = false;
            if (counts.SessionId != source.SessionId)
            {
                intervalProblem = "this directory now holds another session";
                ApplyScope(null);
                return;
            }

            KeepBesideCounts(family, measured);

            ApplyScope(counts, interval, standIn: false);
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            // A newer brush or a closed workspace superseded this count; nothing is applied.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && ReferenceEquals(intervalQuery, query))
            {
                intervalLoading = false;
                intervalProblem = exception.Message;
                ApplyScope(null);
            }
        }
    }

    /// <summary>
    /// Shows interval counts: this generation's own once read, or an earlier publication's standing in while they are.
    /// A stand-in keeps the ranking, graph and tables on the scope the user chose instead of blinking back to whole-
    /// session numbers for the length of a count (§6.4); it is marked pending and replaced, never merged.
    /// </summary>
    private void ApplyScope(SessionIntervalCounts? counts, TimeRange? counted, bool standIn)
    {
        displayedCounts = counts;
        displayedCountsInterval = counts is null ? null : counted;
        scopeStandsIn = standIn && counts is not null;
        ApplyScope(counts is null ? null : OverviewWorkspace.WithinInterval(wholeSnapshot, counts));
    }

    /// <summary>Re-projects the ranking and the relationship table over the scoped or whole snapshot, keeping the selected row.</summary>
    private void ApplyScope(WorkspaceSnapshot? scoped)
    {
        string? rowKey = selectedRung?.Key;
        scopedSnapshot = scoped;
        if (scoped is null)
        {
            displayedCounts = null;
            displayedCountsInterval = null;
            scopeStandsIn = false;
        }

        appliedInterval = scoped is null || scopeStandsIn ? null : scopedInterval;
        if (scoped is null)
        {
            graphDisplay = wholeDisplay;
            if (wholeDisplay.Nodes.Count > 0 || wholeDisplay.TotalProcesses == 0)
            {
                SetGraphProjectionProblem(null);
            }
        }
        else
        {
            graphDisplay = ScopeGraph(wholeDisplay, scoped);
        }
        view = ProjectLadder();
        OnPropertyChanged(nameof(GraphDisplay));
        OnPropertyChanged(nameof(GraphSummary));
        selectedRung = IsEvidenceRung ? selectedRung : RungRows.FirstOrDefault(row => row.Key == rowKey);
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(IsRankedWithinInterval));
        OnPropertyChanged(nameof(RankingScopeText));
        RestateRelationships();
        OnPropertyChanged(nameof(RelationshipTableScope));
        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(SelectedRung));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(IsEmptyRung));
        OnPropertyChanged(nameof(ShowsRankedTable));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(EvidenceSummary));

        // A byte ranking follows the scope: its bytes are read for the new one unless they came with its counts. So do a
        // process's RPC channels and a channel's calls.
        RaiseRankingChanged();
        RankingReady = FollowRankedMeasuresAsync();
        SyncRpcScope();
        SyncHttpScope();
        SyncConnectionsScope();
        FollowDescribedBytes();
    }

    /// <summary>
    /// The applied snapshot an export names: this generation, this rung and its filters, and the interval the shown
    /// counts answer. At the evidence rung the scope is the records' own, and the export is complete only when every
    /// page of that scope has been loaded (plan §6.4).
    /// </summary>
    public ExportContext DescribeExport(DateTimeOffset exportedUtc)
    {
        ExportContext context = WorkspaceExport.RankingContext(
            evidenceSource?.SessionId, evidenceSource?.Generation, ladder, appliedInterval, workspaceDisclosure, exportedUtc,
            view.Rows.Any(row => row.Ranked is not null) ? AppliedRanking : RankingMetric.Records);

        // A call ranking says why it could not rank, or the RPC coverage its counts rest on, as icat export does.
        context = RankingExportCaveat is { } caveat ? context with { Caveats = [.. context.Caveats, caveat] } : context;
        return EvidencePolicyCaveat is { } policy ? context with { Caveats = [.. context.Caveats, policy] } : context;
    }

    /// <summary>
    /// The applied view in the chosen format (§6.4). A ranked rung exports its rows. The evidence rung exports its whole
    /// scope, read off the UI thread in one pass up to <see cref="SessionExport.DefaultEvidenceLimit"/> records rather
    /// than only the pages loaded so far, names the generation the records were read in, and says when the limit left
    /// records out. <c>icat export</c> builds the same file through the same contract (R18).
    /// </summary>
    public async Task<SessionExportResult> ExportAsync(
        ExportFormat format, DateTimeOffset exportedUtc, bool redacted = false,
        CancellationToken cancellationToken = default)
    {
        if (IsEvidenceRung && evidence is { Problem: null } list && evidenceSource is { } source)
        {
            SessionEvidencePage read = await source.ReadScopeAsync(list.Scope, SessionExport.DefaultEvidenceLimit, cancellationToken)
                .AnsweredLater();
            ExportContext context = WorkspaceExport.EvidenceContext(read.SessionId, read.Generation, ladder, list.Scope,
                read.NextCursor is null, workspaceDisclosure,
                string.Create(CultureInfo.InvariantCulture,
                    $"Only the first {read.Records.Count:N0} records of this scope are included; narrow it with a filter or a brushed interval, or use icat export --limit for more."),
                exportedUtc);
            if (EvidencePolicyCaveat is { } policy)
            {
                context = context with { Caveats = [.. context.Caveats, policy] };
            }

            string content = redacted
                ? RedactedShareExport.Evidence(format, context, read.Records)
                : WorkspaceExport.Evidence(format, context, read.Records);
            return new(content, context, read.Records.Count);
        }

        // A stand-in is an earlier publication's answer; the export names this generation, so it waits for its own counts,
        // through a brush or two changed meanwhile, and refuses rather than wait on a count that no longer runs.
        for (int wait = 0; scopeStandsIn; wait++)
        {
            if (disposed || wait == 4)
            {
                throw new InvalidOperationException(
                    "This publication's interval counts are still being read; export again once the ranking says it is ranked.");
            }

            await IntervalReady.WaitAsync(cancellationToken);
        }

        // Measures standing in from an earlier publication are waited past the same way: the export names this generation.
        for (int wait = 0; MeasuresStandIn; wait++)
        {
            if (disposed || wait == 4)
            {
                throw new InvalidOperationException(
                    "This publication's ranking is still being read; export again once the ranking note stops saying so.");
            }

            await RankingReady.WaitAsync(cancellationToken);
        }

        ExportContext ranked = DescribeExport(exportedUtc);
        string rankedContent = redacted
            ? RedactedShareExport.Ranking(format, ranked, view.Rows)
            : WorkspaceExport.Ranking(format, ranked, view.Rows);
        return new(rankedContent, ranked, view.Rows.Count);
    }

    /// <summary>Whether the rung shows anything to export: ranked rows, or a readable evidence scope that holds records.</summary>
    public bool CanExport => IsEvidenceRung ? evidence is { Problem: null, Records.Count: > 0 } : view.Rows.Count > 0;

    private void OnSelectionChanged(object? sender, WorkspaceSelection changed)
    {
        selectedInterval = changed.Interval;
        SyncIntervalScope();
        RaiseScopeChanged();
        if (changed.ProcessId is null)
        {
            selectedProcess = null;
        }
        else if (selectedProcess?.Id != changed.ProcessId)
        {
            // A process chosen elsewhere replaces a selected group or aggregate, as choosing it in the graph does.
            selectedProcess = Snapshot.Processes.FirstOrDefault(process => process.Id == changed.ProcessId);
            selectedClusterKey = null;
            selectedGroupKey = null;
            chosenChannelKey = null;
            chosenProcesses.Clear();
            graphSelection = null;
            describesRow = false;
            ForgetRelationship();
        }

        OnPropertyChanged(nameof(SelectedProcess));
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(IntervalLabel));
        RaiseGraphSelectionChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
        if (propertyName == nameof(GraphDisplay))
        {
            // The legend keys the unmeasured value only while a pane draws one (§6.6).
            PropertyChanged?.Invoke(this, new(nameof(GraphDrawsUnmeasured)));
            PropertyChanged?.Invoke(this, new(nameof(DrawsUnmeasured)));
        }
        else if (propertyName is nameof(ShowsMechanismLanes) or nameof(ShowsProcessLanes) or nameof(ShowsDirectionLanes)
            or nameof(ShowsChannelEndLanes) or nameof(TimelineBytes) or nameof(ProcessLaneBytes) or nameof(DirectionLaneBytes))
        {
            // The legend's height key follows the lanes drawn and what they plot (§6.2, §6.8).
            PropertyChanged?.Invoke(this, new(nameof(OffersLaneScale)));
            PropertyChanged?.Invoke(this, new(nameof(LaneScaleText)));
        }
    }
}
