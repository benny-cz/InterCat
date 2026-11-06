using System.Collections.ObjectModel;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>A way the window can group a session's processes (§6.3), as the Group by selector lists it.</summary>
public sealed record GroupingOption(LaneGrouping Grouping, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Grouping switch
    {
        LaneGrouping.UserSession => "Terminal session: group the processes by the terminal session their lifecycle records name",
        LaneGrouping.ServiceContainer => "Service container: each process hosting the services the capture listed apart, every "
            + "other process by the executable its records name",
        _ => "Executable: group the processes by the executable their records name",
    };
}

/// <summary>
/// The ranked table's grouping selector (§6.3, §6's lane grouping): whether the machine rung's rows, the graph's groups and a
/// group's timeline gather the processes by executable, by terminal session, or by service container - each host of the
/// services its capture listed apart, every other process by its executable (ADR-096). The window regroups the overview it
/// already projected, so a choice reads nothing again; a grouping is offered once the session's records say what it needs.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private static readonly ReadOnlyCollection<GroupingOption> GroupingChoices = Array.AsReadOnly(
    [
        new GroupingOption(LaneGrouping.Executable, "Executable"),
        new GroupingOption(LaneGrouping.UserSession, "Terminal session"),
        new GroupingOption(LaneGrouping.ServiceContainer, "Service container"),
    ]);

    /// <summary>What the selector says each grouping gathers, on hover and to a screen reader.</summary>
    public const string GroupingDefinition =
        "Executable gathers the processes whose records name one executable. Terminal session gathers those whose lifecycle "
        + "records name one terminal session: session 0 holds the machine's services, and each signed-in user has their "
        + "own. Service container sets each process the capture's list of running services names as hosting one apart, named "
        + "by its services, and keeps every other process with its executable. A process no record names what a grouping "
        + "needs for is grouped apart, never under a guess. Choosing reads nothing again, and the counts, ranking and time "
        + "stay as they are.";

    private IReadOnlyList<GroupingOption>? groupingOptions;

    /// <summary>
    /// The groupings this session offers: by executable always, by terminal session once one of its processes' lifecycle
    /// records names one, and by service container once its capture listed the services running.
    /// </summary>
    public IReadOnlyList<GroupingOption> GroupingOptions => groupingOptions ??=
        [.. GroupingChoices.Where(option => WorkspaceGrouping.Offered(wholeSnapshot).Contains(option.Grouping))];

    /// <summary>How the processes are grouped now.</summary>
    public LaneGrouping Grouping => WorkspaceGrouping.Of(wholeSnapshot);

    /// <summary>The grouping as the selector shows it.</summary>
    public GroupingOption SelectedGrouping => GroupingChoices.First(option => option.Grouping == Grouping);

    /// <summary>
    /// Whether the machine rung offers the selector: its rows are the groups, so a choice regroups what is shown. A session
    /// whose records name no terminal session and whose capture listed no services has one grouping, and no selector.
    /// </summary>
    public bool ShowsGroupingChoice => evidenceSource is not null && ladder.Current.Level == DetailLevel.Machine
        && GroupingOptions.Count > 1;

    /// <summary>
    /// A group as a sentence names it: "executable group", "terminal session group", "service container group", or a
    /// plain "group" for the processes a listed service may have run in.
    /// </summary>
    private static string GroupNoun(ProcessGroup group) =>
        group.Kind == LaneGrouping.UserSession ? "terminal session group"
        : WorkspaceGrouping.IsContainer(group) ? "service container group"
        : group.Key == WorkspaceGrouping.ServiceHostUnknownKey ? "group"
        : "executable group";

    /// <summary>
    /// What a group node's members share, as its card says it: "3 processes of this executable", "of this terminal
    /// session", "hosting these services", or, for the processes no lifecycle record names a session for or a listed
    /// service may have run in, that no record tells.
    /// </summary>
    private string GroupMembers(GraphDisplayNode node)
    {
        string processes = Counted(node.Members.Count, "process", "processes");
        ProcessGroup? group = wholeSnapshot.Groups.FirstOrDefault(candidate => candidate.Key == node.GroupKey);
        return group switch
        {
            { Kind: LaneGrouping.UserSession, Key: WorkspaceGrouping.UnrecordedSessionKey } =>
                processes + " whose terminal session no record names",
            { Kind: LaneGrouping.UserSession } => processes + " of this terminal session",
            { Key: WorkspaceGrouping.ServiceHostUnknownKey } => processes + " a listed service may have run in",
            { } container when WorkspaceGrouping.IsContainer(container) => processes + " hosting these services",
            _ => processes + " of this executable",
        };
    }
}
