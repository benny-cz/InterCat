using System.Globalization;
using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>
/// The group a bound process instance belongs to under a process grouping (`contracts/metrics-v1.md` §6): itself, its
/// witnessed executable, the terminal session it ran in, or the package it ran in - or why it belongs to none, which a
/// grouped answer reports as unattributed rather than guessing a group.
/// </summary>
public static class ProcessGrouping
{
    /// <summary>Whether a grouping puts each record under a process instance's group.</summary>
    public static bool ByProcess(LaneGrouping? grouping) => ByInstanceFact(grouping) || grouping is LaneGrouping.Peer;

    /// <summary>
    /// Whether a grouping groups instances by a fact of each one - itself, its executable, its terminal session or its package -
    /// so a count of peers or channels can be made for each group.
    /// </summary>
    public static bool ByInstanceFact(LaneGrouping? grouping) =>
        grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.UserSession or LaneGrouping.Package;

    /// <summary>Why <paramref name="instance"/> belongs to no group under <paramref name="grouping"/>, or null when it belongs to one.</summary>
    public static ProcessBindingReason? Unattributable(ProcessInstance instance, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable when string.IsNullOrWhiteSpace(instance.ImagePath) => ProcessBindingReason.ExecutableUnknown,
            LaneGrouping.UserSession when instance.SessionId is null => ProcessBindingReason.SessionUnknown,
            LaneGrouping.Package when instance.PackageFullName is null => ProcessBindingReason.PackageUnknown,
            _ => null,
        };
    }

    /// <summary>
    /// The stable key of the group <paramref name="instance"/> belongs to: its identity, its witnessed path folded
    /// case-insensitively, its terminal session, or its package. Only for an instance <see cref="Unattributable"/> finds a group for.
    /// </summary>
    public static string KeyOf(ProcessInstance instance, LaneGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable => instance.ImagePath!.ToUpperInvariant(),
            LaneGrouping.UserSession => SessionKey(instance.SessionId!.Value),
            LaneGrouping.Package => PackageKey(instance.PackageFullName!),
            _ => instance.Id.ToString(),
        };
    }

    /// <summary>A terminal session's group key, which sorts sessions by their number.</summary>
    public static string SessionKey(uint session) => "SESSION:" + session.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>
    /// A package's group key, its full name folded as Windows compares package names; the processes in no package share the
    /// key of the empty name, a group of its own.
    /// </summary>
    public static string PackageKey(string package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return "PACKAGE:" + package.ToUpperInvariant();
    }

    /// <summary>
    /// Why a generation's processes cannot be grouped this way at all, or null when they can: when no instance names a
    /// full image path, a terminal session or a package, every group would be one unattributed reason.
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
            LaneGrouping.Package when !processes.Instances.Any(instance => instance.PackageFullName is not null) =>
                "No process lifecycle record in this generation names the package its process ran in, or none, so no "
                + "process can be grouped by package; a package is never guessed from a path or a name.",
            _ => null,
        };
    }
}
