using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

public sealed class WorkspaceViewModel : INotifyPropertyChanged
{
    private readonly WorkspaceSelectionCoordinator selection = new();
    private ProcessNode? selectedProcess;
    private TimeRange? selectedInterval;

    public WorkspaceViewModel()
    {
        Snapshot = SyntheticWorkspace.Create();
        selection.SelectionChanged += OnSelectionChanged;
        selectedProcess = Snapshot.Processes[0];
        selection.SelectProcess(selectedProcess.Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceSnapshot Snapshot { get; }

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
        ? "Choose a node, ranked process, or timeline bucket."
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
