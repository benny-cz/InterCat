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

/// <summary>How an export is written: self-describing JSON, or CSV that repeats its context in every row.</summary>
public enum ExportFormat
{
    Json,
    Csv,
}

/// <summary>One labelled fact about the selected evidence record, as the inspector lists it.</summary>
public sealed record EvidenceField(string Label, string Value);

/// <summary>UI intent that can be rebased onto a later published generation of the same session.</summary>
public sealed record WorkspaceNavigationMemento(
    IReadOnlyList<NavigationState> Breadcrumb,
    ProcessInstanceId? SelectedProcess,
    TimeRange? SelectedInterval,
    string? SelectedRungKey,
    bool ShowTables);

public sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GraphLayoutScheduler graphLayout = new();
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
    private TimeRange? scopedInterval;

    // The interval the displayed counts actually answer; a brush still being counted is not yet applied.
    private TimeRange? appliedInterval;
    private CancellationTokenSource? intervalQuery;
    private bool intervalLoading;
    private string? intervalProblem;
    private IReadOnlyList<RelationshipRow> relationships;
    private CancellationTokenSource? timelineQuery;
    private (TimeRange Viewport, int Columns)? requestedTimeline;
    private SessionTimelineDetail? timelineDetail;
    private IReadOnlyList<IntervalRow> intervals;

    public WorkspaceViewModel() : this(SyntheticWorkspace.Create(), "synthetic-tour-v1")
    {
    }

    /// <summary>
    /// Presents one immutable workspace. The graph identity must name the same data revision as the snapshot,
    /// so an off-thread layout from an older revision can never replace its coordinates.
    /// </summary>
    public WorkspaceViewModel(WorkspaceSnapshot snapshot, string graphIdentity, SessionEvidenceSource? evidenceSource = null)
    {
        wholeSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        realOverview = graphIdentity.StartsWith("session:", StringComparison.Ordinal);
        emptyWorkspace = graphIdentity == "empty-workspace";
        this.evidenceSource = realOverview ? evidenceSource : null;
        workspaceDisclosure = graphIdentity == "synthetic-tour-v1"
            ? "Synthetic interaction tour; none of these values are Windows capture evidence."
            : graphIdentity == "empty-workspace"
                ? "No live capture is running. Start exploring to see published evidence."
                : "Graph and channel rungs show admitted paired TCP only; the timeline includes every observed row. "
                    + "TCP and UDP records are completed transfers, so this session has no operation rung: source "
                    + "records are one step (E) from every rung, and Enter on a record opens its original journal "
                    + "entry. Byte previews stay hidden until requested.";
        // The snapshot's saved positions are a first-frame fallback. The complete layout is computed off-thread
        // and applied only if its identity is still the graph the window is showing.
        GraphPositions = new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(
            Snapshot.Processes.ToDictionary(node => node.Id, node => new GraphPoint(node.X, node.Y)));
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

        LayoutReady = BuildLayoutAsync(graphIdentity);
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
        if (disposed || requestedTimeline == (viewport, columns))
        {
            return;
        }

        requestedTimeline = (viewport, columns);
        timelineQuery?.Cancel();
        timelineQuery?.Dispose();
        timelineQuery = null;
        TimeRange extent = wholeSnapshot.Extent;
        if (evidenceSource is null || (viewport.StartTicks <= extent.StartTicks && viewport.EndTicks >= extent.EndTicks))
        {
            SetTimelineDetail(null);
            TimelineDetailReady = Task.CompletedTask;
            return;
        }

        var query = new CancellationTokenSource();
        timelineQuery = query;
        TimelineDetailReady = LoadTimelineDetailAsync(evidenceSource, viewport, columns, query);
    }

    private async Task LoadTimelineDetailAsync(
        SessionEvidenceSource source, TimeRange viewport, int columns, CancellationTokenSource query)
    {
        try
        {
            SessionTimelineDetail detail = await source.TimelineAsync(viewport, columns, query.Token);
            if (!disposed && ReferenceEquals(timelineQuery, query))
            {
                SetTimelineDetail(detail.SessionId == source.SessionId ? detail : null);
            }
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            // A newer viewport or a closed workspace superseded this count.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && ReferenceEquals(timelineQuery, query))
            {
                SetTimelineDetail(null);
            }
        }
    }

    private void SetTimelineDetail(SessionTimelineDetail? detail)
    {
        if (ReferenceEquals(timelineDetail, detail))
        {
            return;
        }

        timelineDetail = detail;
        intervals = WorkspaceRowBuilder.Intervals(detail?.Buckets ?? wholeSnapshot.Timeline, ThemeMode.Dark);
        if (selectedIntervalRow is { } row && !intervals.Contains(row))
        {
            // The analysis interval stays selected; only the table row that named it is gone at this resolution.
            selectedIntervalRow = null;
            OnPropertyChanged(nameof(SelectedIntervalRow));
        }

        OnPropertyChanged(nameof(TimelineDetail));
        OnPropertyChanged(nameof(Intervals));
        OnPropertyChanged(nameof(IntervalTableScope));
    }

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
        selectedProcess?.Id, selectedInterval, selectedRung?.Key, showTables);

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

    /// <summary>Stable graph coordinates, separate from evidence, time scope and the window transform.</summary>
    public IReadOnlyDictionary<ProcessInstanceId, GraphPoint> GraphPositions { get; private set; }

    /// <summary>Completes when this workspace's initial off-thread layout has applied or reported a problem.</summary>
    public Task LayoutReady { get; }

    public string? GraphLayoutProblem { get; private set; }

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

    private async Task BuildLayoutAsync(string graphIdentity)
    {
        try
        {
            GraphLayoutResult? result = await graphLayout.RequestAsync(
                graphIdentity, Snapshot.Groups, Snapshot.Processes, Snapshot.Edges,
                previous: GraphPositions);
            if (disposed || result is null || result.GraphIdentity != graphIdentity)
            {
                return;
            }

            GraphPositions = result.Positions;
            OnPropertyChanged(nameof(GraphPositions));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            GraphLayoutProblem = exception.Message;
            OnPropertyChanged(nameof(GraphLayoutProblem));
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
        ? "Enter opens the original record · M loads more · Esc goes back"
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
            OnPropertyChanged();
            selection.SelectProcess(value?.Id);
        }
    }

    public TimeRange? SelectedInterval => selectedInterval;

    public string SelectionTitle => selectedProcess is null ? "Nothing selected" : selectedProcess.Name;

    public string SelectionSubtitle => selectedProcess is null
        ? "Choose a node, ranked row, or timeline bucket."
        : $"PID {selectedProcess.ProcessId.ToString("N0", CultureInfo.CurrentCulture)} · {selectedProcess.Role}";

    public string IntervalLabel => selectedInterval is { } interval
        ? WorkspaceTime.FormatRange(interval, CultureInfo.CurrentCulture)
        : "All " + WorkspaceTime.FormatDuration(Snapshot.Extent.EndTicks - Snapshot.Extent.StartTicks, CultureInfo.CurrentCulture);

    public string EvidenceSummary
    {
        get
        {
            if (selectedProcess is null)
            {
                return "No evidence selected";
            }

            CommunicationEdge[] edges = Snapshot.Edges
                .Where(edge => edge.SourceId == selectedProcess.Id || edge.TargetId == selectedProcess.Id)
                .ToArray();
            long observations = edges.Sum(edge => edge.ObservationCount);
            if (realOverview)
            {
                return edges.Length == 0
                    ? "No admitted paired TCP relationship for this process. Other activity may be present."
                    : $"{observations:N0} paired TCP observations · bytes unknown";
            }
            long knownBytes = edges.Where(edge => edge.KnownBytes.HasValue).Sum(edge => edge.KnownBytes!.Value);
            return $"{observations:N0} observations · {knownBytes / 1_000_000m:N2} MB known";
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
        LadderDescent descent = realOverview && ladder.Current.Level == DetailLevel.Machine && selectedProcess is { } process
            ? LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
                new(DetailLevel.ProcessInstance, process.Id.ToString(), process.Name),
                "Evidence was reached from the machine rung with this process selected.")
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

    public void ClearSelection() => selection.Clear();

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
                $"{EvidenceRowText.ProviderName(row.ProviderId)} · event {row.EventId} v{row.DescriptorVersion}")));
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

        evidenceRows = [.. list.Records.Select(EvidenceRow)];
    }

    private static RungRow EvidenceRow(SessionEvidenceRecord record)
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
            size ?? string.Empty, tokens.Label, tokens.Glyph, string.Empty, "original record", source)
        {
            SpokenName = $"{title}{(size is null ? string.Empty : ", " + size)}, at {when}{endpoints}, owned by {owner}. "
                + "Press Enter to open the original record.",
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
        view = LadderProjection.Project(Snapshot, ladder.Current);
        relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeMode.Dark);
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
    public ExportContext DescribeExport(DateTimeOffset exportedUtc)
    {
        bool evidenceRung = IsEvidenceRung && evidence is not null;
        string scope = evidenceRung
            ? evidence!.Scope.Description
            : appliedInterval is { } range
                ? "Ranked within " + WorkspaceTime.FormatRange(range, CultureInfo.InvariantCulture)
                : "Whole session";
        bool complete = !evidenceRung
            || (evidence!.NextCursor is null && !evidence.Loading && evidence.Problem is null);
        List<string> caveats = [workspaceDisclosure];
        if (evidenceRung)
        {
            caveats.Add(complete
                ? "Every record of this scope is included."
                : "Only the records loaded so far are included; load more before exporting for the rest of the scope.");
        }

        return new(
            evidenceSource?.SessionId,
            evidenceSource?.Generation,
            ladder.Current.Level,
            LadderProjection.Breadcrumb(ladder),
            [.. ladder.Current.Filters],
            evidenceRung ? evidence!.Scope.Interval : appliedInterval,
            scope,
            complete,
            caveats,
            exportedUtc);
    }

    /// <summary>What the rung shows, in the chosen format: its ranked rows, or the evidence records loaded at the evidence rung.</summary>
    public string Export(ExportFormat format, DateTimeOffset exportedUtc)
    {
        ExportContext context = DescribeExport(exportedUtc);
        if (IsEvidenceRung && evidence is { } list)
        {
            SessionEvidenceRecord[] records = [.. list.Records];
            return format == ExportFormat.Csv
                ? WorkspaceExport.EvidenceCsv(context, records)
                : WorkspaceExport.EvidenceJson(context, records);
        }

        return format == ExportFormat.Csv
            ? WorkspaceExport.RankingCsv(context, view.Rows)
            : WorkspaceExport.RankingJson(context, view.Rows);
    }

    /// <summary>How many rows an export of the current rung holds.</summary>
    public int ExportRowCount => IsEvidenceRung ? evidence?.Records.Count ?? 0 : view.Rows.Count;

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
            selectedProcess = Snapshot.Processes.FirstOrDefault(process => process.Id == changed.ProcessId);
        }

        OnPropertyChanged(nameof(SelectedProcess));
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(IntervalLabel));
        OnPropertyChanged(nameof(EvidenceSummary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
