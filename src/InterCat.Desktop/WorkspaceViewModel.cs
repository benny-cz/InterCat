using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

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

    public WorkspaceViewModel() : this(SyntheticWorkspace.Create(), "synthetic-tour-v1")
    {
    }

    /// <summary>
    /// Presents one immutable workspace. The graph identity must name the same data revision as the snapshot,
    /// so an off-thread layout from an older revision can never replace its coordinates.
    /// </summary>
    public WorkspaceViewModel(WorkspaceSnapshot snapshot, string graphIdentity)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        realOverview = graphIdentity.StartsWith("session:", StringComparison.Ordinal);
        emptyWorkspace = graphIdentity == "empty-workspace";
        workspaceDisclosure = graphIdentity == "synthetic-tour-v1"
            ? "Synthetic interaction tour; none of these values are Windows capture evidence."
            : graphIdentity == "empty-workspace"
                ? "No live capture is running. Start exploring to see published evidence."
                : "Graph and channel rungs show admitted paired TCP only. Timeline includes other observed rows; "
                    + "the L4-L5 ladder is not yet projected. The source-row inspector and read-only icat "
                    + "channels/evidence queries remain available without implying operation pairing.";
        // The snapshot's saved positions are a first-frame fallback. The complete layout is computed off-thread
        // and applied only if its identity is still the graph the window is showing.
        GraphPositions = new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(
            Snapshot.Processes.ToDictionary(node => node.Id, node => new GraphPoint(node.X, node.Y)));
        ladder = new(SyntheticWorkspace.Root(Snapshot));
        view = LadderProjection.Project(Snapshot, ladder.Current);
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeMode.Dark);
        Relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeMode.Dark);
        Intervals = WorkspaceRowBuilder.Intervals(Snapshot, ThemeMode.Dark);
        selection.SelectionChanged += OnSelectionChanged;
        selectedProcess = !realOverview && Snapshot.Processes.Count > 0 ? Snapshot.Processes[0] : null;
        if (selectedProcess is not null)
        {
            selection.SelectProcess(selectedProcess.Id);
        }

        LayoutReady = BuildLayoutAsync(graphIdentity);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceSnapshot Snapshot { get; }

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
            if (target.Level == DetailLevel.Evidence
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
        graphLayout.Dispose();
    }

    /// <summary>Mechanism legend with glyphs, the redundant channel beside hue (R14).</summary>
    public IReadOnlyList<LegendEntry> Legend { get; }

    /// <summary>Table equivalent of the graph. It yields the same relationships the canvas draws (R15).</summary>
    public IReadOnlyList<RelationshipRow> Relationships { get; }

    /// <summary>Table equivalent of the timeline, with the same counts and coverage states (R15).</summary>
    public IReadOnlyList<IntervalRow> Intervals { get; }

    /// <summary>The ranked table of the rung the user is on. The same gesture works at every rung.</summary>
    public IReadOnlyList<RungRow> RungRows => LadderRowBuilder.Rows(view, ThemeMode.Dark);

    /// <summary>The breadcrumb. It always names the level and the selection at each rung (section 3.2).</summary>
    public IReadOnlyList<CrumbRow> Crumbs => LadderRowBuilder.Crumbs(ladder);

    /// <summary>Filters the descents implied, shown where they can be seen and removed.</summary>
    public IReadOnlyList<FilterRow> Filters => LadderRowBuilder.Filters(ladder.Current);

    public bool HasFilters => ladder.Current.Filters.Count > 0;

    /// <summary>The rung's level as a short badge, so the position is visible without reading the crumbs.</summary>
    public string LevelBadge => string.Create(
        CultureInfo.CurrentCulture,
        $"L{(int)ladder.Current.Level} · {NavigationState.Name(ladder.Current.Level).ToUpperInvariant()}");

    public string LevelSummary => LadderRowBuilder.DescribeTotal(view)
        + (realOverview ? " · admitted paired TCP only; not all session observations" : string.Empty);

    /// <summary>The same total in one line, for the narrow ranked-table rail.</summary>
    public string LevelSummaryShort => LadderRowBuilder.DescribeTotalShort(view)
        + (realOverview ? " · paired TCP only" : string.Empty);

    /// <summary>Why this rung is empty, naming the source that would supply it. Empty when it has rows.</summary>
    public string EmptyReason
    {
        get
        {
            if (view.EmptyReason is null) return string.Empty;
            if (emptyWorkspace) return "No capture is running. Start exploring to publish a live session.";
            if (realOverview && ladder.Current.Level >= DetailLevel.Channel)
            {
                return "This ladder has not projected completion-paired operations or source-record rows yet. "
                    + "Return to the machine, process, or channel context, then choose Inspect "
                    + "source rows for a bounded page of "
                    + "admitted normalized observations. The saved session also supports icat evidence --json.";
            }

            if (realOverview && ladder.Current.Level == DetailLevel.ProcessInstance)
            {
                return Snapshot.ChannelProjectionProblem is { } problem
                    ? problem + " Choose Browse paired channels here to page this process's admitted channels "
                        + "and inspect a selected channel's source rows."
                    : "No admitted paired TCP channel belongs to this process in this generation. "
                        + "One-sided, ambiguous and other-mechanism observations may still exist in the timeline.";
            }

            return view.EmptyReason;
        }
    }

    public bool IsEmptyRung => view.EmptyReason is not null;

    public bool CanAscend => ladder.CanAscend;

    public string AscendLabel => ladder.CanAscend
        ? $"Back to {ladder.Breadcrumb[^2].Crumb} (Esc)"
        : "Clear selection (Esc)";

    public string DescendHint => ladder.Current.Level == DetailLevel.Evidence
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

    public string IntervalLabel => selectedInterval is null
        ? $"All {(Snapshot.Extent.EndTicks - (decimal)Snapshot.Extent.StartTicks) / WorkspaceTime.TicksPerSecond:N1} seconds"
        : $"{selectedInterval.Value.StartTicks / (decimal)WorkspaceTime.TicksPerSecond:N1}s – "
            + $"{selectedInterval.Value.EndTicks / (decimal)WorkspaceTime.TicksPerSecond:N1}s";

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

    /// <summary>Jumps to evidence in one step, from whichever rung the user is on (section 3.2).</summary>
    public bool ShowEvidence()
    {
        if (ladder.Current.Level == DetailLevel.Evidence)
        {
            return false;
        }

        LadderDescent descent = LadderProjection.EvidenceDescentFor(
            ladder.Current,
            selectedInterval ?? ladder.Current.Viewport);
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
    }

    private void OnSelectionChanged(object? sender, WorkspaceSelection changed)
    {
        selectedInterval = changed.Interval;
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
