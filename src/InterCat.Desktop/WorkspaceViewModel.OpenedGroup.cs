using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// A group's rung, with nothing chosen there, describes the group it opened, as an opened channel's rung describes its
/// channel (§6.3, R15): its name, how its processes are drawn there, how they were grouped and the connections they made
/// and accepted, and for a program the programs they talked with, the other hosts they reached and the ports they accepted
/// connections at - what choosing it on the machine rung said. Opening a program otherwise left the inspector saying
/// "Nothing selected" over the processes it had just described, and its lists a rung up. The group is described, not
/// chosen: nothing is highlighted for it, and Esc climbs back as before.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The group the current rung opened, where this generation still holds it; null at any rung but a group's.</summary>
    private ProcessGroup? OpenedGroup => ladder.Current is { Level: DetailLevel.Group, Focus: { } focus } ? Groups(focus.Key) : null;

    /// <summary>
    /// The group the inspector describes: the one chosen, or with nothing chosen at a group's rung, the one it opened; null
    /// for any other selection.
    /// </summary>
    private ProcessGroup? DescribedGroup => SelectedGroup ?? (DescribesNothing ? OpenedGroup : null);

    /// <summary>
    /// Comes back to the group a way down was made from, by its key: nothing chosen where it is the group this rung opened,
    /// whose description that is, else the group chosen again, with its row on the machine rung.
    /// </summary>
    private void ReturnToGroup(string key)
    {
        if (string.Equals(OpenedGroup?.Key, key, StringComparison.Ordinal))
        {
            ClearSelection();
            return;
        }

        SelectGroup(key, syncRow: true);
    }
}
