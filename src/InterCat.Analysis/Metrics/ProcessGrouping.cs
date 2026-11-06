using System.Globalization;
using InterCat.Domain;

namespace InterCat.Analysis;

/// <summary>
/// The group a bound process instance belongs to under a process grouping (`contracts/metrics-v1.md` §6): itself, its
/// witnessed executable, the terminal session it ran in, the package it ran in, or the service container it is - or why it
/// belongs to none, which a grouped answer reports as unattributed rather than guessing a group.
/// </summary>
public static class ProcessGrouping
{
    /// <summary>Whether a grouping puts each record under a process instance's group.</summary>
    public static bool ByProcess(LaneGrouping? grouping) => ByInstanceFact(grouping) || grouping is LaneGrouping.Peer;

    /// <summary>
    /// Whether a grouping groups instances by a fact of each one - itself, its executable, its terminal session, its package
    /// or the services it hosted - so a count of peers or channels can be made for each group.
    /// </summary>
    public static bool ByInstanceFact(LaneGrouping? grouping) =>
        grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.UserSession or LaneGrouping.Package
            or LaneGrouping.ServiceContainer;

    /// <summary>
    /// Why <paramref name="instance"/> belongs to no group under <paramref name="grouping"/>, or null when it belongs to one.
    /// A grouping by service container reads <paramref name="services"/>, the capture's services bound to its instances.
    /// </summary>
    public static ProcessBindingReason? Unattributable(ProcessInstance instance, LaneGrouping grouping, ServiceMatch? services = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable when string.IsNullOrWhiteSpace(instance.ImagePath) => ProcessBindingReason.ExecutableUnknown,
            LaneGrouping.UserSession when instance.SessionId is null => ProcessBindingReason.SessionUnknown,
            LaneGrouping.Package when instance.PackageFullName is null => ProcessBindingReason.PackageUnknown,

            // A container is its own group; any other process keeps its executable's, unless a listed service may have run
            // in it and the binding cannot tell (ADR-096).
            LaneGrouping.ServiceContainer => Required(services).ContainerOf(instance.Id) is not null ? null
                : services!.MayHaveHosted(instance) ? ProcessBindingReason.ServiceHostUnknown
                : string.IsNullOrWhiteSpace(instance.ImagePath) ? ProcessBindingReason.ExecutableUnknown
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// The stable key of the group <paramref name="instance"/> belongs to: its identity, its witnessed path folded
    /// case-insensitively, its terminal session, its package, or its container - a service container's own key, any other
    /// process its executable's. Only for an instance <see cref="Unattributable"/> finds a group for.
    /// </summary>
    public static string KeyOf(ProcessInstance instance, LaneGrouping grouping, ServiceMatch? services = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return grouping switch
        {
            LaneGrouping.Executable => instance.ImagePath!.ToUpperInvariant(),
            LaneGrouping.UserSession => SessionKey(instance.SessionId!.Value),
            LaneGrouping.Package => PackageKey(instance.PackageFullName!),
            LaneGrouping.ServiceContainer => Required(services).ContainerOf(instance.Id) is not null
                ? ContainerKey(instance.Id)
                : instance.ImagePath!.ToUpperInvariant(),
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

    /// <summary>A service container's group key, its host instance's identity; no executable's path begins so.</summary>
    public static string ContainerKey(ProcessInstanceId host) => "CONTAINER:" + host;

    /// <summary>
    /// Why a generation's processes cannot be grouped this way at all, or null when they can: when no instance names a
    /// full image path, a terminal session or a package, every group would be one unattributed reason, and a capture that
    /// kept no list of its services names no container.
    /// </summary>
    public static string? Underived(ProcessInstanceIndex processes, LaneGrouping grouping, ServiceMatch? services = null)
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
            LaneGrouping.ServiceContainer when !Required(services).Recorded =>
                "This capture kept no list of the services running as it started or stopped, so no process can be grouped "
                + "by service container; a service's host is never guessed from a process's name or its image.",
            _ => null,
        };
    }

    private static ServiceMatch Required(ServiceMatch? services) => services
        ?? throw new ArgumentNullException(nameof(services), "A grouping by service container reads the capture's services.");
}
