using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class WorkspaceViewModel : INotifyPropertyChanged
{
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

    public WorkspaceViewModel()
    {
        Snapshot = SyntheticWorkspace.Create();
        ladder = new(SyntheticWorkspace.Root(Snapshot));
        view = LadderProjection.Project(Snapshot, ladder.Current);
        Legend = WorkspaceRowBuilder.Legend(Snapshot, ThemeMode.Dark);
        Relationships = WorkspaceRowBuilder.Relationships(Snapshot, ThemeMode.Dark);
        Intervals = WorkspaceRowBuilder.Intervals(Snapshot, ThemeMode.Dark);
        selection.SelectionChanged += OnSelectionChanged;
        selectedProcess = Snapshot.Processes[0];
        selection.SelectProcess(selectedProcess.Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceSnapshot Snapshot { get; }

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

    public string LevelSummary => LadderRowBuilder.DescribeTotal(view);

    /// <summary>The same total in one line, for the narrow ranked-table rail.</summary>
    public string LevelSummaryShort => LadderRowBuilder.DescribeTotalShort(view);

    /// <summary>Why this rung is empty, naming the source that would supply it. Empty when it has rows.</summary>
    public string EmptyReason => view.EmptyReason ?? string.Empty;

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
        ? "All 24 seconds"
        : $"{selectedInterval.Value.StartTicks / 10_000_000m:N1}s – {selectedInterval.Value.EndTicks / 10_000_000m:N1}s";

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
