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

    public string AccessibleName => Grouping == LaneGrouping.UserSession
        ? "Terminal session: group the processes by the terminal session their lifecycle records name"
        : "Executable: group the processes by the executable their records name";
}

/// <summary>
/// The ranked table's grouping selector (§6.3, §6's lane grouping): whether the machine rung's rows, the graph's groups and a
/// group's timeline gather the processes by executable or by terminal session. The window regroups the overview it already
/// projected, so a choice reads nothing again; it is offered once a process's lifecycle records name a session.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private static readonly ReadOnlyCollection<GroupingOption> GroupingChoices = Array.AsReadOnly(
    [
        new GroupingOption(LaneGrouping.Executable, "Executable"),
        new GroupingOption(LaneGrouping.UserSession, "Terminal session"),
    ]);

    /// <summary>What the selector says each grouping gathers, on hover and to a screen reader.</summary>
    public const string GroupingDefinition =
        "Executable gathers the processes whose records name one executable. Terminal session gathers those whose lifecycle "
        + "records name one terminal session: session 0 holds the machine's services, and each signed-in user has their "
        + "own. A process no record names one for is grouped apart, never under a guess. Choosing reads nothing again, and "
        + "the counts, ranking and time stay as they are.";

    private IReadOnlyList<GroupingOption>? groupingOptions;

    /// <summary>
    /// The groupings this session offers: by executable always, and by terminal session once one of its processes'
    /// lifecycle records names one.
    /// </summary>
    public IReadOnlyList<GroupingOption> GroupingOptions => groupingOptions ??=
        [.. GroupingChoices.Where(option => WorkspaceGrouping.Offered(wholeSnapshot).Contains(option.Grouping))];

    /// <summary>How the processes are grouped now.</summary>
    public LaneGrouping Grouping => WorkspaceGrouping.Of(wholeSnapshot);

    /// <summary>The grouping as the selector shows it.</summary>
    public GroupingOption SelectedGrouping => GroupingChoices.First(option => option.Grouping == Grouping);

    /// <summary>
    /// Whether the machine rung offers the selector: its rows are the groups, so a choice regroups what is shown. A session
    /// whose records name no terminal session has one grouping, and no selector.
    /// </summary>
    public bool ShowsGroupingChoice => evidenceSource is not null && ladder.Current.Level == DetailLevel.Machine
        && GroupingOptions.Count > 1;

    /// <summary>A group as a sentence names it: "executable group", or "terminal session group".</summary>
    private static string GroupNoun(ProcessGroup group) =>
        group.Kind == LaneGrouping.UserSession ? "terminal session group" : "executable group";

    /// <summary>
    /// What a group node's members share, as its card says it: "3 processes of this executable", "of this terminal
    /// session", or, for the processes no lifecycle record names a session for, that none does.
    /// </summary>
    private string GroupMembers(GraphDisplayNode node)
    {
        string processes = Counted(node.Members.Count, "process", "processes");
        ProcessGroup? group = wholeSnapshot.Groups.FirstOrDefault(candidate => candidate.Key == node.GroupKey);
        return group is not { Kind: LaneGrouping.UserSession } ? processes + " of this executable"
            : group.Key == WorkspaceGrouping.UnrecordedSessionKey ? processes + " whose terminal session no record names"
            : processes + " of this terminal session";
    }
}
