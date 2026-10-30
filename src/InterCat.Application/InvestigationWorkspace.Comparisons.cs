using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// One interval a saved comparison names (`contracts/workspace-v23.md` §7): a member session's, half-open in its own
/// presentation ticks, as `icat compare` takes it.
/// </summary>
public sealed record SavedComparisonInterval
{
    /// <summary>The member whose time the interval is in.</summary>
    public required Guid SessionId { get; init; }

    public required long StartTicks { get; init; }

    public required long EndTicks { get; init; }

    /// <summary>The interval; null where its end is not after its start.</summary>
    [JsonIgnore]
    public TimeRange? Interval => TimeRange.TryCreate(StartTicks, EndTicks, out TimeRange interval) ? interval : null;
}

/// <summary>
/// One revision of a saved comparison specification (plan M12): a named comparison of two intervals of the investigation's
/// members - one session's two, or two captures' - with what each row is compared by and what it is of, or its removal.
/// The name keeps it; a later revision of the same name replaces or removes it, and every revision is kept. It holds no
/// answer: run, it is read afresh from the sessions (`contracts/comparison-v1.md`).
/// </summary>
public sealed record SavedComparison
{
    public required int Revision { get; init; }

    public required string Name { get; init; }

    /// <summary>The baseline, an interval of a member; null when this revision removes the comparison.</summary>
    public SavedComparisonInterval? Baseline { get; init; }

    /// <summary>The compared interval, of the baseline's member or of another capture; null when this revision removes it.</summary>
    public SavedComparisonInterval? Compared { get; init; }

    /// <summary>What each row is compared by; null when this revision removes the comparison.</summary>
    public ComparisonMetric? Metric { get; init; }

    /// <summary>What each row is of: processes, or executables, which two captures compare by alone; null when it is removed.</summary>
    public ComparisonGrouping? GroupedBy { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }

    /// <summary>Whether its two intervals are of two captures rather than one session's.</summary>
    [JsonIgnore]
    public bool AcrossCaptures => Baseline is { } baseline && Compared is { } compared && baseline.SessionId != compared.SessionId;

    /// <summary>
    /// Whether it compares just what <paramref name="comparison"/> did (`contracts/workspace-v23.md` §7): the same two
    /// intervals of the same sessions, by the same metric, its rows of the same grouping - so a note about it, or about one of
    /// its rows by key, is about that comparison's.
    /// </summary>
    public bool Describes(IntervalComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return Baseline is { } before && Compared is { } after
            && before.SessionId == (comparison.BaselineSessionId ?? comparison.SessionId) && before.Interval == comparison.Baseline
            && after.SessionId == comparison.SessionId && after.Interval == comparison.Compared
            && Metric == comparison.Metric && GroupedBy == comparison.Grouping;
    }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The longest a saved comparison's name may be, in characters.</summary>
    public const int MostComparisonNameCharacters = 100;

    /// <summary>Why a saved comparison of two captures cannot be of processes.</summary>
    public const string CapturesByExecutable = "Two captures compare by executable: a process of one capture is never another's.";

    /// <summary>
    /// Saves a comparison of <paramref name="compared"/> with <paramref name="baseline"/>, each an interval of a member, by
    /// <paramref name="metric"/> and <paramref name="grouping"/>, named <paramref name="name"/>, replacing a comparison of
    /// that name. Two captures compare by executable alone.
    /// </summary>
    public static SavedComparison SaveComparison(
        string workspacePath,
        string name,
        SavedComparisonInterval baseline,
        SavedComparisonInterval compared,
        ComparisonMetric metric,
        ComparisonGrouping grouping,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(compared);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        string named = ComparisonName(name);
        foreach (SavedComparisonInterval side in new[] { baseline, compared })
        {
            if (workspace.Members.All(member => member.SessionId != side.SessionId))
            {
                throw new InvalidOperationException($"Session {Short(side.SessionId)} is not a session of this investigation.");
            }

            if (side.Interval is null)
            {
                throw new InvalidOperationException("A compared interval ends after it starts.");
            }
        }

        if (baseline.SessionId != compared.SessionId && grouping != ComparisonGrouping.Executable)
        {
            throw new InvalidOperationException(CapturesByExecutable);
        }

        var comparison = new SavedComparison
        {
            Revision = NextComparisonRevision(workspace),
            Name = named,
            Baseline = baseline,
            Compared = compared,
            Metric = metric,
            GroupedBy = grouping,
            RecordedUtc = now,
        };
        Save(full, workspace with { Comparisons = [.. workspace.Comparisons, comparison], UpdatedUtc = now }, file);
        return comparison;
    }

    /// <summary>Removes the saved comparison named <paramref name="name"/>; its revisions are kept.</summary>
    public static SavedComparison RemoveComparison(string workspacePath, string name, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string file) = Load(full);
        SavedComparison current = ComparisonNamed(workspace, name);
        var removal = new SavedComparison { Revision = NextComparisonRevision(workspace), Name = current.Name, RecordedUtc = now };
        Save(full, workspace with { Comparisons = [.. workspace.Comparisons, removal], UpdatedUtc = now }, file);
        return removal;
    }

    /// <summary>
    /// Every saved comparison in force - each name's latest revision, when that does not remove it - in the order first
    /// saved.
    /// </summary>
    public static IReadOnlyList<SavedComparison> ComparisonsInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Comparisons
            .GroupBy(comparison => comparison.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(comparison => comparison.Revision))
            .Select(group => group.MaxBy(comparison => comparison.Revision)!)
            .Where(comparison => comparison.Baseline is not null)];
    }

    /// <summary>
    /// The comparison in force that compares just what <paramref name="comparison"/> did (<see cref="SavedComparison.Describes"/>):
    /// the one named <paramref name="preferred"/>, in any case, where it does, else the first saved; null where none does.
    /// </summary>
    public static SavedComparison? SavedAs(InvestigationWorkspaceFile workspace, IntervalComparison comparison, string? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return ComparisonsInForce(workspace)
            .Where(saved => saved.Describes(comparison))
            .OrderByDescending(saved => string.Equals(saved.Name, preferred, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>The saved comparison in force named <paramref name="name"/>, in any case; refused in words when there is none.</summary>
    public static SavedComparison ComparisonNamed(InvestigationWorkspaceFile workspace, string name)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string trimmed = name?.Trim() ?? string.Empty;
        return ComparisonsInForce(workspace).FirstOrDefault(comparison => string.Equals(comparison.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(NoneNamed("comparison", trimmed, ComparisonsInForce(workspace).Select(comparison => comparison.Name)));
    }

    private static string ComparisonName(string name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= MostComparisonNameCharacters
            ? trimmed
            : throw new InvalidOperationException($"A comparison is named, in at most {MostComparisonNameCharacters} characters.");
    }

    private static int NextComparisonRevision(InvestigationWorkspaceFile workspace) =>
        workspace.Comparisons.Count == 0 ? 1 : checked(workspace.Comparisons.Max(comparison => comparison.Revision) + 1);

    /// <summary>What makes a file's saved comparisons contradict themselves, or null (`contracts/workspace-v23.md` §7).</summary>
    private static string? ComparisonProblem(InvestigationWorkspaceFile workspace)
    {
        // Saved comparisons arrived with the twenty-second version (revision 758).
        if (workspace.Comparisons.Count > 0 && VersionOf(workspace) < 22)
        {
            return $"a {workspace.Contract} file holds no saved comparison";
        }

        if (workspace.Comparisons.Any(comparison => comparison is null))
        {
            return "it lists an empty comparison";
        }

        if (workspace.Comparisons.GroupBy(comparison => comparison.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1) is { } revision)
        {
            return $"comparison revision {revision.Key} is not a unique positive number";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        bool Named(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MostComparisonNameCharacters && name == name.Trim();
        bool Kept(SavedComparison comparison) =>
            comparison.Baseline is { } baseline && comparison.Compared is { } compared
            && comparison.Metric is { } metric && Enum.IsDefined(metric)
            && comparison.GroupedBy is { } grouping && Enum.IsDefined(grouping)
            && baseline.Interval is not null && compared.Interval is not null
            && members.Contains(baseline.SessionId) && members.Contains(compared.SessionId)
            && (!comparison.AcrossCaptures || grouping == ComparisonGrouping.Executable);
        bool Removed(SavedComparison comparison) =>
            comparison.Baseline is null && comparison.Compared is null && comparison.Metric is null && comparison.GroupedBy is null;
        return workspace.Comparisons.FirstOrDefault(comparison => !Named(comparison.Name) || !(Kept(comparison) || Removed(comparison))) is { } wrong
            ? $"comparison revision {wrong.Revision} is not named, compares no two intervals of its sessions, compares by no "
                + "metric or grouping, or compares two captures by process"
            : null;
    }
}
