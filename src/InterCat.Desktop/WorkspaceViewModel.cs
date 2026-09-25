using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;


/// <summary>One labelled fact about the selected evidence record, as the inspector lists it.</summary>
public sealed record EvidenceField(string Label, string Value);

/// <summary>What a hover over a drawn node or edge states (§6.2's hover contract, §6.3): a title and one fact per line.</summary>
public sealed record GraphHoverCard(string Title, IReadOnlyList<string> Lines);

/// <summary>UI intent that can be rebased onto a later published generation of the same session.</summary>
/// <param name="SelectedGraphAggregate">A selected aggregate node that is not one executable group, by its stable key.</param>
public sealed record WorkspaceNavigationMemento(
    IReadOnlyList<NavigationState> Breadcrumb,
    ProcessInstanceId? SelectedProcess,
    TimeRange? SelectedInterval,
    string? SelectedRungKey,
    bool ShowTables,
    string? SelectedGraphAggregate = null);

/// <summary>
/// What one publication's timeline drew beyond the overview: its zoomed detail and the counts of the focus it was drawn
/// for, by that focus's key. The next publication of the same session shows them until its own counts arrive.
/// </summary>
public sealed record TimelineCarry(
    SessionTimelineDetail? Detail,
    string? FocusKey,
    IReadOnlyList<TimelineBucket>? Focus);

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

    // The interval the displayed counts actually answer; a brush still being counted is not yet applied.
    private TimeRange? appliedInterval;
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
    private bool timelineFocusLoading;
    private string? timelineFocusProblem;
    private IReadOnlyList<IntervalRow> intervals;

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
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeMode.Dark);
        relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeMode.Dark);
        intervals = WorkspaceRowBuilder.Intervals(Snapshot, ThemeMode.Dark);
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
                SetTimelineDetail(sameSession && !whole ? counted.Whole : null, sameSession ? counted.Focus : null);
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

    private void SetTimelineDetail(SessionTimelineDetail? detail, IReadOnlyList<TimelineBucket>? focus)
    {
        if (ReferenceEquals(timelineDetail, detail) && ReferenceEquals(timelineFocusBuckets, focus))
        {
            return;
        }

        timelineDetail = detail;
        timelineFocusBuckets = focus;
        intervals = WorkspaceRowBuilder.Intervals(detail?.Buckets ?? wholeSnapshot.Timeline, ThemeMode.Dark, focus);
        if (selectedIntervalRow is { } row)
        {
            // The analysis interval stays selected. Its row follows a focus count arriving at the same resolution, and
            // only a row gone at a new resolution is dropped.
            selectedIntervalRow = intervals.FirstOrDefault(candidate => candidate.Interval == row.Interval);
            OnPropertyChanged(nameof(SelectedIntervalRow));
        }

        OnPropertyChanged(nameof(TimelineDetail));
        OnPropertyChanged(nameof(TimelineFocusBuckets));
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

        // The previous focus's counts describe other records; the whole timeline beside them is still true.
        SetTimelineDetail(timelineDetail, null);
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(TimelineShowsFocus));
        if (drawnTimeline is { } drawn)
        {
            RequestTimelineDetail(drawn.Viewport, drawn.Columns);
        }
    }

    /// <summary>What this workspace's timeline drew, for the next publication of the same session to show until its own counts arrive.</summary>
    public TimelineCarry CarryTimeline() => new(timelineDetail, timelineFocus?.Key, timelineFocusBuckets);

    /// <summary>
    /// Shows an earlier publication's zoomed detail and focus counts until this generation's own arrive, so a live
    /// refresh does not blink the timeline back to coarse bars or drop the focus's colour for the length of a count.
    /// The focus counts are kept only for the same focus; a stand-in is replaced, never merged.
    /// </summary>
    public void AdoptTimeline(TimelineCarry carry)
    {
        ArgumentNullException.ThrowIfNull(carry);
        IReadOnlyList<TimelineBucket>? focus = carry.FocusKey is not null && carry.FocusKey == timelineFocus?.Key ? carry.Focus : null;
        SetTimelineDetail(carry.Detail, focus);
        OnPropertyChanged(nameof(TimelineCaption));
    }

    /// <summary>
    /// The focus's counts, one per bucket of the whole timeline drawn over the same interval: the overview's buckets at
    /// the whole extent, the zoomed detail's when zoomed. Null at a rung without a focus and until its count arrives.
    /// </summary>
    public IReadOnlyList<TimelineBucket>? TimelineFocusBuckets => timelineFocusBuckets;

    /// <summary>Whether the timeline draws the rung's focus in colour over the rest of the machine in grey.</summary>
    public bool TimelineShowsFocus => timelineFocus is not null && timelineFocusProblem is null;

    /// <summary>What the timeline draws, stated above it: every record, or the rung's focus over the rest of the machine.</summary>
    public string TimelineCaption => timelineFocusDescription is not { } focus
        ? "Observed records · Shift+drag brushes a range · unknown stays unknown"
        : timelineFocusProblem is { } problem
            ? $"{focus} could not be counted: {problem.TrimEnd('.')}. Every observed record is shown."
            : timelineFocusLoading && timelineFocusBuckets is null
                ? $"Counting {char.ToLowerInvariant(focus[0])}{focus[1..]}… · the rest of the machine in grey"
                : $"{focus} in colour, the rest of the machine in grey · Shift+drag brushes a range";

    /// <summary>Whether the ranking is scoped to the brushed interval rather than the whole session.</summary>
    public bool IsRankedWithinInterval => scopedSnapshot is not null;

    /// <summary>What the ranking counts, stated wherever totals appear so a brushed number is never read as a session total.</summary>
    public string RankingScopeText
    {
        get
        {
            if (evidenceSource is null || scopedInterval is not { } interval) return string.Empty;
            string range = WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture);
            return intervalProblem is { } problem ? $"Could not rank within {range}: {problem}"
                : intervalLoading ? $"Ranking within {range}…"
                : $"Ranked within {range} · Esc at the machine rung clears it";
        }
    }

    public string WorkspaceDisclosure => workspaceDisclosure;

    /// <summary>Captures navigation independently of an evidence generation, for a same-session refresh only.</summary>
    public WorkspaceNavigationMemento CaptureNavigation() => new(
        [.. ladder.Breadcrumb.Select(rung => rung with { Filters = [.. rung.Filters] })],
        selectedProcess?.Id, selectedInterval, selectedRung?.Key, showTables, selectedClusterKey);

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

        AfterNavigation();
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

        return notices.Count == 0 ? null : string.Join(" ", notices);
    }

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
                ? "no relationship observed"
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

    private (IReadOnlySet<string> Whole, IReadOnlySet<string> Part) GraphSelection()
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
        string[] path = channels.Length == 1
            ? [source.GroupKey, source.Id.ToString(), channels[0].Key]
            : [source.GroupKey, source.Id.ToString()];
        if (!ladder.TryReturnTo(0, out _))
        {
            return false;
        }

        foreach (string key in path)
        {
            LadderRow? row = LadderProjection.Project(Snapshot, ladder.Current).Rows.FirstOrDefault(candidate => candidate.Key == key);
            if (row is null
                || !ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, selectedInterval ?? ladder.Current.Viewport), out _))
            {
                break;
            }
        }

        AfterNavigation();
        return true;
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
            AndItsPeers(string.Create(CultureInfo.InvariantCulture, $"{process.Name} · PID {process.ProcessId}"),
                neighborhood.Count > 1));
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
    public IReadOnlyList<LegendEntry> Legend { get; }

    /// <summary>Table equivalent of the graph. It yields the same relationships the canvas draws (R15).</summary>
    public IReadOnlyList<RelationshipRow> Relationships => relationships;

    /// <summary>Table equivalent of the timeline, with the same counts and coverage states (R15).</summary>
    /// <summary>
    /// The table equivalent of the timeline (R15): the buckets it draws, which are the zoomed viewport's own once they
    /// have arrived and the whole session's otherwise.
    /// </summary>
    public IReadOnlyList<IntervalRow> Intervals => intervals;

    /// <summary>What the interval table lists, so a zoomed table is never read as the whole session.</summary>
    public string IntervalTableScope => timelineDetail is { } detail
        ? string.Create(CultureInfo.CurrentCulture,
            $"Zoomed view {WorkspaceTime.FormatRange(detail.Interval, CultureInfo.CurrentCulture)} in {detail.Buckets.Count:N0} intervals")
        : string.Create(CultureInfo.CurrentCulture, $"Whole session in {intervals.Count:N0} intervals");

    /// <summary>
    /// The ranked table of the rung the user is on. The same gesture works at every rung. At a published session's
    /// evidence rung it lists the admitted source records of the rung's scope, in reading order, as they load.
    /// </summary>
    public IReadOnlyList<RungRow> RungRows => IsEvidenceRung ? evidenceRows : LadderRowBuilder.Rows(view, ThemeMode.Dark);

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
            if (emptyWorkspace) return "No capture is running. Start exploring to publish a live session.";
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

    public string DescendHint => IsEvidenceRung
        ? $"Enter opens the {RecordNoun} · M loads more · Esc goes back"
        : ladder.Current.Level == DetailLevel.Evidence
            ? "Evidence is the last rung. Esc returns to where you were."
            : "Enter opens the selected row · E jumps straight to evidence · Esc goes back";

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
    public GraphHoverCard? DescribeGraphHover(string key)
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
            string title = node.ProcessId is { } pid
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
            $"{ThemePalette.TokensFor(ThemeMode.Dark, ThemePalette.FamilyOf(drawn.Mechanism)).Label} · {DescribeStrength(drawn.Strength)} evidence · "
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

    public string IntervalLabel => selectedInterval is { } interval
        ? WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture)
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
        if (!ladder.TryDescend(descent, out _))
        {
            return false;
        }

        AfterNavigation();
        return true;
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

        TimeRange viewport = selectedInterval ?? ladder.Current.Viewport;
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
        if (!ladder.TryDescend(descent, out _))
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
            selectedInterval ?? ladder.Current.Viewport,
            new(DetailLevel.Channel, channel.Key, channel.Name),
            "Evidence was reached from a channel chosen in the channel list.");
        if (!ladder.TryDescend(descent, out _))
        {
            return false;
        }

        AfterNavigation();
        return true;
    }

    /// <summary>Ascends to exactly where the user was, or clears the selection at the machine rung.</summary>
    public bool Ascend()
    {
        if (!ladder.TryAscend(out NavigationState restored))
        {
            ClearSelection();
            return false;
        }

        selectedInterval = restored.Viewport == Snapshot.Extent ? null : restored.Viewport;
        AfterNavigation();
        return true;
    }

    public void ReturnTo(int depth)
    {
        if (ladder.TryReturnTo(depth, out _))
        {
            AfterNavigation();
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
        OnPropertyChanged(nameof(CanAscend));
        OnPropertyChanged(nameof(AscendLabel));
        OnPropertyChanged(nameof(DescendHint));
        OnPropertyChanged(nameof(IntervalLabel));
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
    /// The graph edge that contributes to what the rung shows: the focused channel's edge at the channel rung, and the
    /// scope's channel at the evidence rung. Null when the rung is not one channel.
    /// </summary>
    public string? HighlightedEdgeKey
    {
        get
        {
            if (IsEvidenceRung) return evidence?.Scope.ContributingEdgeKey;
            if (ladder.Current.Level != DetailLevel.Channel || ladder.Current.Focus is not { } focus) return null;
            return Snapshot.Channels.FirstOrDefault(channel => channel.Key == focus.Key)?.EdgeKey;
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
        FamilyTokens tokens = ThemePalette.TokensFor(ThemeMode.Dark, ThemePalette.FamilyOf(row.Mechanism));
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
        OnPropertyChanged(nameof(EmptyReason));
        OnPropertyChanged(nameof(EvidenceStatus));
        OnPropertyChanged(nameof(EvidenceScopeText));
        OnPropertyChanged(nameof(CanLoadMoreEvidence));
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
        if (evidenceSource is null || selectedInterval == scopedInterval)
        {
            return;
        }

        intervalQuery?.Cancel();
        intervalQuery?.Dispose();
        intervalQuery = null;
        scopedInterval = selectedInterval;
        intervalProblem = null;
        if (selectedInterval is not { } interval)
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

            ApplyScope(OverviewWorkspace.WithinInterval(wholeSnapshot, counts));
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

    /// <summary>Re-projects the ranking and the relationship table over the scoped or whole snapshot, keeping the selected row.</summary>
    private void ApplyScope(WorkspaceSnapshot? scoped)
    {
        string? rowKey = selectedRung?.Key;
        scopedSnapshot = scoped;
        appliedInterval = scoped is null ? null : scopedInterval;
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
        relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeMode.Dark);
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
