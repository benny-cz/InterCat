using System.Globalization;
using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>
/// The group a bound process instance belongs to under a process grouping (`contracts/metrics-v1.md` §6): itself, its
/// witnessed executable, or the terminal session it ran in - or why it belongs to none, which a grouped answer reports
/// as unattributed rather than guessing a group.
/// </summary>
public static class ProcessGrouping
{
    /// <summary>Whether a grouping puts each record under a process instance's group.</summary>
    public static bool ByProcess(LaneGrouping? grouping) =>
        grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.UserSession or LaneGrouping.Peer;

    /// <summary>Why <paramref name="instance"/> belongs to no group under <paramref name="grouping"/>, or null when it belongs to one.</summary>
    public static ProcessBindingReason? Unattributable(ProcessInstance instance, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable when string.IsNullOrWhiteSpace(instance.ImagePath) => ProcessBindingReason.ExecutableUnknown,
            LaneGrouping.UserSession when instance.SessionId is null => ProcessBindingReason.SessionUnknown,
            _ => null,
        };
    }

    /// <summary>
    /// The stable key of the group <paramref name="instance"/> belongs to: its identity, its witnessed path folded
    /// case-insensitively, or its terminal session. Only for an instance <see cref="Unattributable"/> finds a group for.
    /// </summary>
    public static string KeyOf(ProcessInstance instance, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable => instance.ImagePath!.ToUpperInvariant(),
            LaneGrouping.UserSession => SessionKey(instance.SessionId!.Value),
            _ => instance.Id.ToString(),
        };
    }

    /// <summary>A terminal session's group key, which sorts sessions by their number.</summary>
    public static string SessionKey(uint session) => "SESSION:" + session.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>
    /// Why a generation's processes cannot be grouped this way at all, or null when they can: when no instance names a
    /// full image path, or a terminal session, every group would be one unattributed reason.
    /// </summary>
    public static string? Underived(ProcessInstanceIndex processes, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return grouping switch
        {
            LaneGrouping.Executable when !processes.Instances.Any(instance => !string.IsNullOrWhiteSpace(instance.ImagePath)) =>
                "No process lifecycle record in this generation carries a full image name. An executable cannot be "
                + "identified from a PID or an exit basename; import evidence with admitted process image names.",
            LaneGrouping.UserSession when !processes.Instances.Any(instance => instance.SessionId is not null) =>
                "No process lifecycle record in this generation names the terminal session its process ran in, so no "
                + "process can be grouped by session; a session is never guessed from a PID or a name.",
            _ => null,
        };
    }
}
