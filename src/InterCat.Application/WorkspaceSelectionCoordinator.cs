using InterCat.Domain;

namespace InterCat.Application;

public sealed record WorkspaceSelection(
    ProcessInstanceId? ProcessId,
    TimeRange? Interval,
    long Revision);

/// <summary>One selection source for graph, timeline, ranking, and inspector panes.</summary>
public sealed class WorkspaceSelectionCoordinator
{
    public WorkspaceSelection Current { get; private set; } = new(null, null, 0);

    public event EventHandler<WorkspaceSelection>? SelectionChanged;

    public void SelectProcess(ProcessInstanceId? processId)
    {
        Publish(Current with { ProcessId = processId });
    }

    public void SelectInterval(TimeRange? interval)
    {
        Publish(Current with { Interval = interval });
    }

    public void Clear()
    {
        Publish(new(null, null, Current.Revision));
    }

    private void Publish(WorkspaceSelection next)
    {
        Current = next with { Revision = checked(Current.Revision + 1) };
        SelectionChanged?.Invoke(this, Current);
    }
}
