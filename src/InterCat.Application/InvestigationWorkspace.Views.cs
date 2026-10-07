using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One revision of a saved view of an investigation (§8.4): a named interval of its time, in presentation ticks of the time
/// reference it was saved under, or its removal. The name keeps it; a later revision of the same name replaces or removes
/// it, and every revision is kept.
/// </summary>
public sealed record WorkspaceView
{
    public required int Revision { get; init; }

    public required string Name { get; init; }

    /// <summary>Where the view starts, in presentation ticks of its reference's clock; null when this revision removes it.</summary>
    public long? StartTicks { get; init; }

    /// <summary>Where it ends, after its start; null when this revision removes it.</summary>
    public long? EndTicks { get; init; }

    /// <summary>The interval shown; null when this revision removes the view.</summary>
    [JsonIgnore]
    public TimeRange? Interval => StartTicks is { } start && EndTicks is { } end && TimeRange.TryCreate(start, end, out TimeRange interval)
        ? interval
        : null;

    /// <summary>The time reference whose clock the interval is in; null when this revision removes the view.</summary>
    public Guid? Reference { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The longest a view's name may be, in characters.</summary>
    public const int MostViewNameCharacters = 100;

    /// <summary>
    /// Saves <paramref name="interval"/> of the investigation's time as a view named <paramref name="name"/>, replacing a view
    /// of that name; it is in the time reference's clock, so it needs one.
    /// </summary>
    public static WorkspaceView SaveView(string workspacePath, string name, TimeRange interval, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        string named = ViewName(name);
        Guid reference = workspace.TimeReference
            ?? throw new InvalidOperationException("The investigation has no time yet - no session is aligned - so no view of it can be saved.");
        var view = new WorkspaceView
        {
            Revision = NextViewRevision(workspace),
            Name = named,
            StartTicks = interval.StartTicks,
            EndTicks = interval.EndTicks,
            Reference = reference,
            RecordedUtc = now,
        };
        Save(full, workspace with { Views = [.. workspace.Views, view], UpdatedUtc = now }, file);
        return view;
    }

    /// <summary>Removes the view named <paramref name="name"/>; its revisions are kept.</summary>
    public static WorkspaceView RemoveView(string workspacePath, string name, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        WorkspaceView current = ViewsInForce(workspace).FirstOrDefault(view => string.Equals(view.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No view of this investigation is named '{name.Trim()}'.");
        var removal = new WorkspaceView { Revision = NextViewRevision(workspace), Name = current.Name, RecordedUtc = now };
        Save(full, workspace with { Views = [.. workspace.Views, removal], UpdatedUtc = now }, file);
        return removal;
    }

    /// <summary>Every view in force - each name's latest revision, when that does not remove it - in the order first saved.</summary>
    public static IReadOnlyList<WorkspaceView> ViewsInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Views
            .GroupBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(view => view.Revision))
            .Select(group => group.MaxBy(view => view.Revision)!)
            .Where(view => view.Interval is not null)];
    }

    /// <summary>Whether a view is in the clock of the investigation's time now: saved under its time reference.</summary>
    public static bool ViewIsCurrent(InvestigationWorkspaceFile workspace, WorkspaceView view)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(view);
        return view.Reference is { } reference && reference == workspace.TimeReference;
    }

    private static string ViewName(string name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= MostViewNameCharacters
            ? trimmed
            : throw new InvalidOperationException($"A view is named, in at most {MostViewNameCharacters} characters.");
    }

    private static int NextViewRevision(InvestigationWorkspaceFile workspace) =>
        workspace.Views.Count == 0 ? 1 : checked(workspace.Views.Max(view => view.Revision) + 1);

    /// <summary>What makes a file's views contradict themselves, or null (`contracts/workspace-v17.md` §7).</summary>
    private static string? ViewProblem(InvestigationWorkspaceFile workspace)
    {
        // Saved views arrived with the tenth version (revision 271).
        if (workspace.Views.Count > 0 && VersionOf(workspace) < 10)
        {
            return $"a {workspace.Contract} file holds no saved view";
        }

        if (workspace.Views.Any(view => view is null))
        {
            return "it lists an empty view";
        }

        if (workspace.Views.GroupBy(view => view.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1) is { } revision)
        {
            return $"view revision {revision.Key} is not a unique positive number";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        return workspace.Views.FirstOrDefault(view => string.IsNullOrWhiteSpace(view.Name) || view.Name.Length > MostViewNameCharacters
            || view.Name != view.Name.Trim()
            || (view.StartTicks is null) != (view.EndTicks is null) || (view.StartTicks is null) != (view.Reference is null)
            || view.StartTicks is { } start && view.EndTicks is { } end && !TimeRange.TryCreate(start, end, out _)
            || view.Reference is { } reference && !members.Contains(reference)) is { } wrong
            ? $"view revision {wrong.Revision} is not named, shows no interval, or is in the clock of no member"
            : null;
    }
}
