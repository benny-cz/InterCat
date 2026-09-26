using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    IReadOnlyList<NavigationState>? Forward = null);

/// <summary>
/// What one publication's ranking counted: the visible range it followed and the interval counts it showed, with the
/// interval they answer. The next publication of the same session shows them, marked pending, until its own arrive.
/// </summary>
public sealed record ScopeCarry(TimeRange? VisibleRange, TimeRange? Interval, SessionIntervalCounts? Counts);

/// <summary>
/// What one publication's timeline drew beyond the overview: its zoomed detail and the counts of the focus it was drawn
/// for, by that focus's key. The next publication of the same session shows them until its own counts arrive.
/// </summary>
public sealed record TimelineCarry(
    SessionTimelineDetail? Detail,
    string? FocusKey,
    IReadOnlyList<TimelineBucket>? Focus,
    IReadOnlyList<ProcessTimelineLane>? ProcessLanes = null,
    string? ProcessLaneProblem = null,
    IReadOnlyList<DirectionTimelineLane>? DirectionLanes = null,
    IReadOnlyList<ChannelEndTimelineLane>? ChannelEndLanes = null);

public sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
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
                    ? OverviewWorkspace.SessionDisclosure
                    : OverviewWorkspace.RedactedDisclosure + " " + OverviewWorkspace.SessionDisclosure;
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
        view = LadderProjection.Project(Snapshot, ladder.Current);
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeResources.CurrentMode);
        relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeResources.CurrentMode);
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
        intervals = WorkspaceRowBuilder.Intervals(Snapshot, ThemeResources.CurrentMode);
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
                SessionTimelineDetail detail = await source.TimelineAsync(viewport, columns, query.Token);
                if (!disposed && ReferenceEquals(timelineQuery, query))
                {
                    SetTimelineDetail(detail.SessionId == source.SessionId ? detail : null, null);
                }

                return;
            }

            SessionFocusedTimeline counted = await source.FocusedTimelineAsync(viewport, columns, focus, query.Token);
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
    }

    private IReadOnlyList<ProcessTimelineLane> OrderProcessLanes(IReadOnlyList<ProcessTimelineLane> lanes)
    {
        Dictionary<ProcessInstanceId, int> processIds = wholeSnapshot.Processes
            .ToDictionary(process => process.Id, process => process.ProcessId);
        return [.. lanes.OrderBy(lane => processIds.GetValueOrDefault(lane.ProcessId, int.MaxValue))
            .ThenBy(lane => lane.ProcessId.Value)];
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
        intervals = WorkspaceRowBuilder.Intervals(buckets, ThemeResources.CurrentMode,
            selected is null && processLane is null && directionLane is null && endLane is null ? focus : null);
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
        if (drawnTimeline is { } drawn)
        {
            RequestTimelineDetail(drawn.Viewport, drawn.Columns);
        }
    }

    /// <summary>What this workspace's ranking counted, for the next publication of the same session to show until its own arrive.</summary>
    public ScopeCarry CarryScope() => new(visibleRange, displayedCountsInterval, displayedCounts);

    /// <summary>
    /// Takes on an earlier publication's visible range, so this generation starts counting it before the view is bound,
    /// and shows that publication's counts while this one's own are read. Nothing stands in once they have arrived, or
    /// when the scope is the whole session.
    /// </summary>
    public void AdoptScope(ScopeCarry carry)
    {
        ArgumentNullException.ThrowIfNull(carry);
        ShowVisibleRange(carry.VisibleRange);
        if (carry.Counts is { } counts && evidenceSource is { } source && counts.SessionId == source.SessionId
            && scopedInterval is not null && intervalLoading && scopedSnapshot is null)
        {
            ApplyScope(counts, carry.Interval, standIn: true);
            OnPropertyChanged(nameof(RankingScopeText));
        }
    }

    /// <summary>What this workspace's timeline drew, for the next publication of the same session to show until its own counts arrive.</summary>
    public TimelineCarry CarryTimeline() => new(timelineDetail, timelineFocus?.Key, timelineFocusBuckets,
        timelineProcessLanes, processLaneProblem, timelineDirectionLanes, timelineChannelEnds);

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
    public string TimelineCaption => timelineFocusDescription is not { } focus
        ? ShowsMechanismLanes
            ? $"Observed records by mechanism · {wholeSnapshot.MechanismLanes.Count:N0} lanes"
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
                ? $"{focus} · {processLaneDisplay.Count:N0} process lanes · machine context above · scroll names for more"
            : ShowsDirectionLanes
                ? $"{focus} · by source direction · machine context above"
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
        OnPropertyChanged(nameof(WorkspaceDisclosure));
    }

    /// <summary>Captures navigation independently of an evidence generation, for a same-session refresh only.</summary>
    public WorkspaceNavigationMemento CaptureNavigation() => new(
        [.. ladder.Breadcrumb.Select(rung => rung with { Filters = [.. rung.Filters] })],
        selectedProcess?.Id, selectedInterval, selectedRung?.Key, showTables, selectedClusterKey,
        searchText, selectedSearchResult?.Hit.Key, SelectedTimelineMechanism, SelectedTimelineDirection,
        selectedChannelEnd, [.. ladder.Forward.Select(rung => rung with { Filters = [.. rung.Filters] })]);

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
                LadderRow? row = LadderProjection.Project(Snapshot, ladder.Current).Rows.FirstOrDefault(
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
    public GraphDisplay GraphDisplay => graphDisplay;

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
                ? text + string.Create(CultureInfo.CurrentCulture, $" · drawn as {graphDisplay.Nodes.Count:N0} nodes")
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

        HashSet<ProcessInstanceId> members = selectedGroupKey is { } group
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
        if (syncRow && ladder.Current.Level == DetailLevel.Machine
            && RungRows.FirstOrDefault(row => row.Key == groupKey) is { } row)
        {
            selectedRung = row;
            OnPropertyChanged(nameof(SelectedRung));
        }

        RaiseGraphSelectionChanged();
    }

    /// <summary>
    /// §6.7's double-click on an edge: opens the relationship's channel view. The ladder descends one rung at a time, so
    /// the path runs from the machine through the source process's group and the process to the relationship's channel,
    /// when it has exactly one; with several it stops at the process, whose rows list them. The breadcrumb states every
    /// rung, and each Esc climbs one. An aggregate edge stands for several relationships and opens nothing.
    /// </summary>
    public bool OpenGraphEdge(string edgeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeKey);
        if (graphDisplay.Edges.FirstOrDefault(edge => edge.Key == edgeKey) is not { Relationships.Count: 1 } drawn
            || wholeSnapshot.Edges.FirstOrDefault(edge => edge.Key == drawn.Relationships[0]) is not { } relationship
            || wholeSnapshot.Processes.FirstOrDefault(process => process.Id == relationship.SourceId) is not { } source)
        {
            return false;
        }

        Channel[] channels = [.. Snapshot.Channels.Where(channel => channel.EdgeKey == relationship.Key)];
        return DescendAlong(channels.Length == 1
            ? [source.GroupKey, source.Id.ToString(), channels[0].Key]
            : [source.GroupKey, source.Id.ToString()], selectedInterval);
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

    /// <summary>Whether the rail explains an empty rung: not while a search lists its hits there.</summary>
    public bool ShowsEmptyReason => IsEmptyRung && !IsSearching;

    public bool ShowsLoadMoreEvidence => CanLoadMoreEvidence && !IsSearching;

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

    private void RaiseGraphSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedGraphNodeKey));
        OnPropertyChanged(nameof(SelectedGraphNodeKeys));
        OnPropertyChanged(nameof(PartlySelectedGraphNodeKeys));
        OnPropertyChanged(nameof(SelectedCluster));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(EvidenceHeading));
        OnPropertyChanged(nameof(EvidenceSummary));
        OnPropertyChanged(nameof(PinActionLabel));
        OnPropertyChanged(nameof(CanTogglePin));
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
    /// and are kept for as long as the session is open, across refreshes and rungs. Workspaces are not saved yet, so
    /// pins do not survive closing the session (§26.3); nothing claims that they do.
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
    /// to report, is a plain statement and must not look like a warning (§6.6).
    /// </summary>
    public bool CoverageLimited => Snapshot.Timeline.Any(bucket => bucket.Coverage != CoverageState.Covered);

    /// <summary>Coverage is derived from the snapshot's intervals, never from a prototype constant.</summary>
    public string CoverageSummary
    {
        get
        {
            if (Snapshot.Timeline.Count == 0)
            {
                return "Coverage not quantified";
            }

            var limitations = new List<string>();
            int gaps = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.PartialGap);
            if (gaps > 0)
            {
                limitations.Add($"{gaps:N0} partial-gap intervals");
            }

            int unknown = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.UnknownCoverage);
            if (unknown > 0)
            {
                limitations.Add($"{unknown:N0} coverage-unknown intervals");
            }

            int omitted = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.NotCollected);
            if (omitted > 0)
            {
                limitations.Add($"{omitted:N0} not-collected intervals");
            }

            int reduced = Snapshot.Timeline.Count(bucket => bucket.Coverage == CoverageState.ReducedFidelity);
            if (reduced > 0)
            {
                limitations.Add($"{reduced:N0} reduced-fidelity intervals");
            }

            return limitations.Count == 0
                ? $"Coverage reported for {Snapshot.Timeline.Count:N0} intervals"
                : string.Join(" · ", limitations) + " · not extrapolated";
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
        intervalQuery?.Cancel();
        intervalQuery?.Dispose();
        intervalQuery = null;
        timelineQuery?.Cancel();
        timelineQuery?.Dispose();
        timelineQuery = null;
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

    /// <summary>Table equivalent of the timeline, with the same counts and coverage states (R15).</summary>
    /// <summary>
    /// The table equivalent of the timeline (R15): the buckets it draws, which are the zoomed viewport's own once they
    /// have arrived and the whole session's otherwise.
    /// </summary>
    public IReadOnlyList<IntervalRow> Intervals => intervals;

    /// <summary>What the interval table lists, so a zoomed table is never read as the whole session.</summary>
    public string IntervalTableScope
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
    public IReadOnlyList<RungRow> RungRows => IsEvidenceRung ? evidenceRows : LadderRowBuilder.Rows(view, ThemeResources.CurrentMode);

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
        : FocusedRealChannel is { } channel
            ? string.Create(CultureInfo.CurrentCulture,
                $"{channel.ObservationCount:N0} observed records at this channel's two ends · bytes unknown · no operation rung; E shows the records")
            : LadderRowBuilder.DescribeTotal(view)
                + (realOverview ? " · admitted paired TCP only; not all session observations" : string.Empty);

    /// <summary>The same total in one line, for the narrow ranked-table rail.</summary>
    public string LevelSummaryShort => IsEvidenceRung
        ? EvidenceStatus
        : FocusedRealChannel is { } channel
            ? string.Create(CultureInfo.CurrentCulture, $"{channel.ObservationCount:N0} records on this channel · no operation rung")
            : LadderRowBuilder.DescribeTotalShort(view) + (realOverview ? " · paired TCP only" : string.Empty);

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

            if (view.EmptyReason is null) return string.Empty;
            if (emptyWorkspace) return awaitingCaptureNote ?? "No capture is running. Start exploring to publish a live session.";
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

    public bool IsEmptyRung => IsEvidenceRung ? evidenceRows.Count == 0 : view.EmptyReason is not null;

    /// <summary>Whether the empty rung can offer its one-step path to evidence as a button beside the reason.</summary>
    public bool OffersEvidenceStep => IsEmptyRung && realOverview && evidenceSource is not null
        && ladder.Current.Level is DetailLevel.ProcessInstance or DetailLevel.Channel or DetailLevel.Operation;

    public bool CanAscend => ladder.CanAscend;

    public string AscendLabel => ladder.CanAscend
        ? $"Back to {ladder.Breadcrumb[^2].Crumb} (Esc)"
        : "Clear selection (Esc)";

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
        }
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

    public RelationshipRow? SelectedRelationship
    {
        get => selectedRelationship;
        set
        {
            selectedRelationship = value;
            OnPropertyChanged();
            if (value is not null)
            {
                SelectProcess(value.SourceId);
            }
        }
    }

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

    public string SelectionTitle => SelectedCluster is { } cluster ? cluster.Label
        : SelectedGroup is { } group ? group.Name
        : selectedProcess is null ? "Nothing selected" : selectedProcess.Name;

    public string SelectionSubtitle => SelectedCluster is { } cluster ? DescribeCluster(cluster)
        : SelectedGroup is { } group ? DescribeGroup(group)
        : selectedProcess is null
            ? "Choose a node, ranked row, or timeline bucket."
            : $"PID {selectedProcess.ProcessId.ToString("N0", CultureInfo.CurrentCulture)} · {selectedProcess.Role}";

    /// <summary>What an aggregate node holds, and where each of its processes can be read one by one.</summary>
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
        string metric = realOverview ? "Paired TCP observations" : "Observations";
        string semantics = realOverview
            ? "Basis: source observations · unit: observations · domain: paired TCP transport evidence · accounting: not applicable to a count; both witnessed endpoints contribute"
            : "Basis: source observations · unit: observations · domain: relationship evidence · accounting: not applicable to a count";
        string scope = appliedInterval is { } interval
            ? "Scope: " + WorkspaceTime.FormatHalfOpenRange(interval, CultureInfo.CurrentCulture) + " · brushed interval"
            : "Scope: " + WorkspaceTime.FormatHalfOpenRange(Snapshot.Extent, CultureInfo.CurrentCulture) + " · whole session";
        if (graphDisplay.Node(key) is { } node)
        {
            long scale = GraphEncoding.NodeScale(graphDisplay);
            string title = node.ProcessId is { } pid && node.Label != ProcessNode.PidName(pid)
                ? string.Create(CultureInfo.CurrentCulture, $"{node.Label} · PID {pid}")
                : node.Label;
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
                HoverBytes(Snapshot.Edges.Where(edge => members.Contains(edge.SourceId) || members.Contains(edge.TargetId)),
                    node.Observations),
                "Coverage: " + DescribeCoverage(coverage),
                node.Kind == GraphNodeKind.Context
                    ? "Size: fixed; the rest of the machine is not read on this focus's scale"
                    : string.Create(CultureInfo.CurrentCulture, $"Size: log scale against the busiest drawn node, {scale:N0}"),
            };
            if (graphPins.ContainsKey(node.Key))
            {
                nodeLines.Add("Pinned where it was placed · P releases it");
            }

            return new(title, nodeLines);
        }

        if (graphDisplay.Edges.FirstOrDefault(edge => edge.Key == key) is not { } drawn)
        {
            return null;
        }

        long edgeScale = graphDisplay.Edges.Max(edge => edge.ObservationCount);
        HashSet<string> relationships = [.. drawn.Relationships];
        Channel[] channels = [.. Snapshot.Channels.Where(channel => relationships.Contains(channel.EdgeKey))];
        var lines = new List<string>
        {
            $"{ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(drawn.Mechanism)).Label} · {DescribeStrength(drawn.Strength)} evidence · "
                + Counted(drawn.Relationships.Count, "relationship", "relationships") + " · "
                + Counted(channels.Length, "channel", "channels"),
            scope,
            semantics,
            string.Create(CultureInfo.CurrentCulture, $"{metric}: {drawn.ObservationCount:N0}")
                + (realOverview ? " · from both ends" : string.Empty)
                + (drawn.ObservationCount == 0 && appliedInterval is not null
                    ? " · none in this interval; the relationship exists in the session"
                    : string.Empty),
            HoverBytes(Snapshot.Edges.Where(edge => relationships.Contains(edge.Key)), drawn.ObservationCount),
        };
        if (realOverview)
        {
            lines.Add("Direction: display order only, not who initiated or sent");
        }

        lines.Add("Coverage: " + DescribeCoverage(channels.Length == 0 ? CoverageState.UnknownCoverage : Worst(channels.Select(channel => channel.Coverage))));
        lines.Add(string.Create(CultureInfo.CurrentCulture, $"Thickness: log scale against the busiest drawn edge, {edgeScale:N0}"));
        if (drawn.Relationships.Count == 1)
        {
            // What OpenGraphEdge will do, so the gesture is discoverable where the edge is read.
            lines.Add(channels.Length switch
            {
                1 => "Double-click opens its channel",
                0 => "Double-click opens its source process",
                _ => string.Create(CultureInfo.CurrentCulture,
                    $"Double-click opens its source process, whose rows list its {channels.Length:N0} channels"),
            });
        }

        string source = graphDisplay.Node(drawn.SourceKey)?.Label ?? drawn.SourceKey;
        string target = graphDisplay.Node(drawn.TargetKey)?.Label ?? drawn.TargetKey;
        return new($"{source} ↔ {target}", lines);
    }

    /// <summary>
    /// Bytes a mark's relationships measured, and how many observations belong to relationships whose byte total is
    /// unknown. The model has relationship-level byte availability, not a per-observation byte-known bit, so the card
    /// states that granularity rather than fabricating a more precise denominator (R21).
    /// </summary>
    private static string HoverBytes(IEnumerable<CommunicationEdge> edges, long observations)
    {
        CommunicationEdge[] all = [.. edges];
        CommunicationEdge[] known = [.. all.Where(edge => edge.KnownBytes.HasValue)];
        long unknownObservations = all.Where(edge => !edge.KnownBytes.HasValue).Sum(edge => edge.ObservationCount);
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
                ? "No record observed · an empty bucket is not proof of inactivity"
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

        lines.Add(ownerLane is not null || directionLane is not null
            ? $"Rate: {TimelineView.RateText(perSecond)} · height against the busiest visible lane including machine context, {TimelineView.RateText(peakPerSecond)} (shared scale)"
            : lane is null
            ? $"Rate: {TimelineView.RateText(perSecond)} · height against the busiest visible bar, {TimelineView.RateText(peakPerSecond)}"
            : $"Rate: {TimelineView.RateText(perSecond)} · height against the busiest mechanism lane in this time view, {TimelineView.RateText(peakPerSecond)} (shared scale)");
        return FinishTimelineHover(bucket, zoomed, lines);
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
                ? "No record observed at this end · an empty bucket is not proof of inactivity"
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
            + $"{TimelineView.RateText((double)inbound * WorkspaceTime.TicksPerSecond / span)} inbound · each band's height "
            + $"against the busiest visible band including machine context, {TimelineView.RateText(peakPerSecond)} (shared scale)");
        return FinishTimelineHover(bucket, timelineDetail is not null && end.Buckets.Contains(bucket), lines);
    }

    /// <summary>What every timeline card closes with: the unmeasured part, bytes, coverage, resolution and the click.</summary>
    private HoverCard FinishTimelineHover(TimelineBucket bucket, bool zoomed, List<string> lines)
    {
        lines.Add("Unmeasured: none in this bucket; a record without a usable session time is placed in no bucket");
        lines.Add(bucket.KnownBytes is { } bytes
            ? "Bytes: " + WorkspaceRowBuilder.DescribeBytes(bytes)
            : "Bytes: unknown · this timeline counts records");
        lines.Add("Coverage: " + DescribeCoverage(bucket.Coverage)
            + (bucket.Coverage == CoverageState.Covered ? string.Empty : " · drawn hatched"));
        lines.Add(zoomed
            ? string.Create(CultureInfo.CurrentCulture, $"Resolution: this view's own count, {timelineDetail!.Buckets.Count:N0} buckets")
                + (timelineDetail.Generation != DisplayedGeneration
                    ? string.Create(CultureInfo.CurrentCulture, $" from generation {timelineDetail.Generation:N0}")
                    : string.Empty)
            : string.Create(CultureInfo.CurrentCulture, $"Resolution: the overview's {Snapshot.Timeline.Count:N0} buckets over the whole session"));
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
    /// The time scope the inspector states: the analysis interval, or the whole extent. An empty workspace has recorded
    /// no time, so its placeholder extent is never read out as a duration.
    /// </summary>
    public string IntervalLabel => selectedInterval is { } interval
        ? WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture)
        : VisibleScope is { } visible ? "Visible " + WorkspaceTime.FormatRange(visible, CultureInfo.CurrentCulture)
        : emptyWorkspace ? "No time recorded yet"
        : "All " + WorkspaceTime.FormatDuration(Snapshot.Extent.EndTicks - Snapshot.Extent.StartTicks, CultureInfo.CurrentCulture);

    /// <summary>What the inspector's evidence line describes: the selected process, group or aggregate.</summary>
    public string EvidenceHeading => SelectedCluster is not null ? "Selected aggregate"
        : SelectedGroup is not null ? "Selected group"
        : "Selected process";

    public string EvidenceSummary
    {
        get
        {
            if (SelectedCluster is { } cluster)
            {
                return cluster.Relationships == 0
                    ? "No admitted paired TCP relationship among these processes. Other activity may be present."
                    : string.Create(CultureInfo.CurrentCulture, $"{cluster.Observations:N0} paired TCP observations on ")
                        + Counted(cluster.Relationships, "relationship", "relationships") + " · bytes unknown";
            }

            HashSet<ProcessInstanceId> scope = SelectedGroup is { } group
                ? [.. Snapshot.Processes.Where(process => process.GroupKey == group.Key).Select(process => process.Id)]
                : selectedProcess is { } process ? [process.Id] : [];
            if (scope.Count == 0)
            {
                return "No evidence selected";
            }

            string subject = SelectedGroup is null ? "this process" : "this group's processes";
            CommunicationEdge[] edges = [.. Snapshot.Edges
                .Where(edge => scope.Contains(edge.SourceId) || scope.Contains(edge.TargetId))];
            long observations = edges.Sum(edge => edge.ObservationCount);
            if (realOverview)
            {
                return edges.Length == 0
                    ? $"No admitted paired TCP relationship for {subject}. Other activity may be present."
                    : SelectedGroup is null
                        ? $"{observations:N0} paired TCP observations · bytes unknown"
                        : string.Create(CultureInfo.CurrentCulture, $"{observations:N0} paired TCP observations on ")
                            + Counted(edges.Length, "relationship", "relationships") + " · bytes unknown";
            }
            // Bytes nothing measured are unknown, never zero (R3): a sum over no known value is no sum at all.
            long? knownBytes = edges.Any(edge => edge.KnownBytes.HasValue)
                ? edges.Where(edge => edge.KnownBytes.HasValue).Sum(edge => edge.KnownBytes!.Value)
                : null;
            return $"{observations:N0} observations · {WorkspaceRowBuilder.DescribeBytes(knownBytes)}";
        }
    }

    /// <summary>Descends one rung from the selected row. One gesture, at every rung (section 3.2).</summary>
    public bool Descend()
    {
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

    /// <summary>Descends one rung, first recording on the rung being left the interval the user had there (§6.7).</summary>
    private bool TryDescend(LadderDescent descent)
    {
        ladder.RecordInterval(selectedInterval);
        return ladder.TryDescend(descent, out _);
    }

    /// <summary>
    /// Jumps to evidence in one step, from whichever rung the user is on (section 3.2). At a published session's
    /// machine rung a selected process is the scope, named in the filter bar where it can be removed.
    /// </summary>
    public bool ShowEvidence()
    {
        if (ladder.Current.Level == DetailLevel.Evidence)
        {
            return false;
        }

        // E lists the records behind the counts on screen, so it reads the same scope they answer (§6.4, I5).
        TimeRange viewport = ScopeInterval ?? ladder.Current.Viewport;
        bool machine = realOverview && ladder.Current.Level == DetailLevel.Machine;
        LadderDescent descent = machine && selectedProcess is { } process
            ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                new(DetailLevel.ProcessInstance, process.Id.ToString(), process.Name),
                "Evidence was reached from the machine rung with this process selected.")
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
        if (!ladder.TryAscend(out NavigationState restored))
        {
            ClearSelection();
            return false;
        }

        ResumeIntervalOf(restored);
        AfterNavigation();
        return true;
    }

    /// <summary>Returns to a rung the breadcrumb names, exactly as the user left it, as an ascent does.</summary>
    public void ReturnTo(int depth)
    {
        ladder.RecordInterval(selectedInterval);
        if (ladder.TryReturnTo(depth, out NavigationState restored))
        {
            ResumeIntervalOf(restored);
            AfterNavigation();
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

    public void SelectProcess(ProcessInstanceId processId)
    {
        ProcessNode? process = Snapshot.Processes.FirstOrDefault(candidate => candidate.Id == processId);
        if (process is not null)
        {
            SelectedProcess = process;
        }
    }

    public void SelectInterval(TimeRange interval) => selection.SelectInterval(interval);

    public void ClearSelection()
    {
        selectedClusterKey = null;
        selectedGroupKey = null;
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
        view = LadderProjection.Project(Snapshot, ladder.Current);
        selectedRung = null;
        selectedCrumb = null;

        // A group selected by its row is left with that row; a descent into it makes it the ladder's focus instead.
        selectedGroupKey = null;
        RefreshGraphDisplay();
        UpdateTimelineFocus();
        RefreshIntervalRows(timelineFocusBuckets);
        SyncEvidence();
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
            if (EvidenceRowText.Size(row, CultureInfo.CurrentCulture) is { } size)
                fields.Add(new("Size", size + (row.ByteDomain is { } domain ? $" · {domain}" : string.Empty)));
            fields.Add(new("Source", string.Create(CultureInfo.InvariantCulture,
                $"{EvidenceRowText.ProviderName(row.ProviderId, wholeSnapshot.Redaction is not null)} · event {row.EventId} v{row.DescriptorVersion}")));
            fields.Add(new("Quality", $"attribution {row.AttributionQuality}, correlation {row.CorrelationQuality}, "
                + $"measurement {row.MeasurementQuality}, timing {row.TimingQuality}"));
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
            if (ladder.Current.Level != DetailLevel.Channel || ladder.Current.Focus is not { } focus) return null;

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
            SessionEvidencePage page = await source.ReadAsync(list.Scope, cursor, list.Cancellation.Token);
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
        string owner = EvidenceRowText.Owner(record, CultureInfo.CurrentCulture);
        string when = EvidenceRowText.When(row, CultureInfo.CurrentCulture);
        string pid = row.OwnerProcessId is { } id ? string.Create(CultureInfo.CurrentCulture, $"PID {id}") : "no owner";
        // The rail is narrow: what happened and its size on the first line, when and whose on the second. Endpoints
        // and the full owner are the inspector's, where they fit without being cut.
        string detail = when + " · " + pid;
        var source = new LadderRow(EvidenceKey(record), title, detail, 1, row.ByteValue, row.Mechanism,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
        string endpoints = EvidenceRowText.Endpoints(row) is { } pair ? ", " + pair : string.Empty;
        return new(EvidenceKey(record), size is null ? title : title + " · " + size, detail, string.Empty,
            size ?? string.Empty, tokens.Label, tokens.Glyph, string.Empty, recordNoun, source)
        {
            SpokenName = $"{title}{(size is null ? string.Empty : ", " + size)}, at {when}{endpoints}, owned by {owner}. "
                + $"Press Enter to open the {recordNoun}.",
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
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(EvidenceMarkTicks));
        OnPropertyChanged(nameof(HighlightedEdgeKey));
        OnPropertyChanged(nameof(SelectedEvidence));
        OnPropertyChanged(nameof(HasSelectedEvidence));
        OnPropertyChanged(nameof(SelectedEvidenceTitle));
        OnPropertyChanged(nameof(SelectedEvidenceFields));
        OnPropertyChanged(nameof(SelectedEvidenceTick));
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
            SessionIntervalCounts counts = await source.CountAsync(interval, query.Token);
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
        view = LadderProjection.Project(Snapshot, ladder.Current);
        relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeResources.CurrentMode);
        OnPropertyChanged(nameof(GraphDisplay));
        OnPropertyChanged(nameof(GraphSummary));
        selectedRung = IsEvidenceRung ? selectedRung : RungRows.FirstOrDefault(row => row.Key == rowKey);
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(IsRankedWithinInterval));
        OnPropertyChanged(nameof(RankingScopeText));
        OnPropertyChanged(nameof(Relationships));
        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(SelectedRung));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(IsEmptyRung));
        OnPropertyChanged(nameof(ShowsRankedTable));
        OnPropertyChanged(nameof(ShowsEmptyReason));
        OnPropertyChanged(nameof(EvidenceSummary));
    }

    /// <summary>
    /// The applied snapshot an export names: this generation, this rung and its filters, and the interval the shown
    /// counts answer. At the evidence rung the scope is the records' own, and the export is complete only when every
    /// page of that scope has been loaded (plan §6.4).
    /// </summary>
    public ExportContext DescribeExport(DateTimeOffset exportedUtc) =>
        WorkspaceExport.RankingContext(
            evidenceSource?.SessionId, evidenceSource?.Generation, ladder, appliedInterval, workspaceDisclosure, exportedUtc);

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
            SessionEvidencePage read = await source.ReadScopeAsync(list.Scope, SessionExport.DefaultEvidenceLimit, cancellationToken);
            ExportContext context = WorkspaceExport.EvidenceContext(read.SessionId, read.Generation, ladder, list.Scope,
                read.NextCursor is null, workspaceDisclosure,
                string.Create(CultureInfo.InvariantCulture,
                    $"Only the first {read.Records.Count:N0} records of this scope are included; narrow it with a filter or a brushed interval, or use icat export --limit for more."),
                exportedUtc);
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
        }

        OnPropertyChanged(nameof(SelectedProcess));
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(IntervalLabel));
        RaiseGraphSelectionChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
